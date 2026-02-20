/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Collections.Generic;
using QuantConnect.Brokerages.Kraken.Models;

namespace QuantConnect.Brokerages.Kraken
{
    /// <summary>
    /// Kraken rate limits implementation
    /// </summary>
    public class KrakenBrokerageRateLimits : IDisposable
    {
        private readonly CancellationTokenSource _cancellationTokenSource;

        // Open Order limits
        private const int OpenOrdersLimitPerTickerSafetyMargin = 10;
        private readonly int _openOrdersRateLimitPerTicker = 0;

        private readonly Dictionary<KrakenVerificationTier, int> _openOrdersRateLimitsPerTicker = new()
        {
            [KrakenVerificationTier.Starter] = 60,
            [KrakenVerificationTier.Intermediate] = 80,
            [KrakenVerificationTier.Pro] = 225,
        };

        // Transaction limits
        private readonly int decayIntervalInMs;
        private readonly Lock _transactionLocker = new();
        private const int TransactionsLimitPerTickerSafetyMargin = 10;
        private readonly ConcurrentDictionary<Symbol, (decimal, DateTime)> _transactionsCountersPerTicker = new();
        private readonly int _transactionsLimitPerTicker = 0;
        private readonly decimal _transactionsDecayPerTicker = 0;

        private readonly Dictionary<KrakenVerificationTier, int> _transactionsLimitsPerTicker = new()
        {
            [KrakenVerificationTier.Starter] = 12,
            [KrakenVerificationTier.Intermediate] = 125,
            [KrakenVerificationTier.Pro] = 180,
        };

        private readonly Dictionary<KrakenVerificationTier, decimal> _transactionsDecayLimitsPerTicker = new()
        {
            [KrakenVerificationTier.Starter] = 1m,
            [KrakenVerificationTier.Intermediate] = 2.34m,
            [KrakenVerificationTier.Pro] = 3.75m,
        };

        /// <summary>
        /// Event that fires when an error is encountered in the brokerage
        /// </summary>
        public event EventHandler<BrokerageMessageEvent> Message;

        /// <summary>
        /// Choosing the right rate limits based on the verification tier
        /// </summary>
        /// <param name="verificationTier">Starter, Intermediate, Pro</param>
        /// <param name="timerInterval">Rate limit timer interval, useful for testing</param>
        public KrakenBrokerageRateLimits(string verificationTier, int timerInterval = 1000)
        {
            decayIntervalInMs = timerInterval;
            _cancellationTokenSource = new CancellationTokenSource();
            if (!Enum.TryParse(verificationTier, true, out KrakenVerificationTier tier))
            {
                var availableTiers = string.Join(", ", Enum.GetNames(typeof(KrakenVerificationTier)));
                throw new ArgumentOutOfRangeException(nameof(verificationTier),
                    $"Invalid verification tier '{verificationTier}'. Available tiers: {availableTiers}");
            }

            _openOrdersRateLimitPerTicker = _openOrdersRateLimitsPerTicker[tier] - OpenOrdersLimitPerTickerSafetyMargin;
            _transactionsLimitPerTicker = _transactionsLimitsPerTicker[tier] - TransactionsLimitPerTickerSafetyMargin;
            _transactionsDecayPerTicker = _transactionsDecayLimitsPerTicker[tier];
        }

        /// <summary>
        /// Checks if the current open orders are less than the tier allowance
        /// </summary>
        /// <param name="symbolOpenOrders">Current open orders per symbol</param>
        /// <exception cref="BrokerageException"> Rate limit exceeded</exception>
        public bool IsWithinOpenOrderLimit(int symbolOpenOrders)
        {
            return symbolOpenOrders < _openOrdersRateLimitPerTicker;
        }

        /// <summary>
        /// Usual Rest request rate limit check
        /// </summary>
        private void TransactionLimitCheck(Symbol symbol, int weight)
        {
            const int maxRetries = 10;
            var retryCount = 0;

            while (!TryUpdateCounter(symbol, weight, DateTime.UtcNow, out var diff))
            {
                if (retryCount >= maxRetries)
                {
                    throw new InvalidOperationException(
                        $"Failed to acquire rate limit slot for {symbol} after {maxRetries} retries. " +
                        "This may indicate excessive concurrent requests.");
                }

                var waitTimeInMs = (Math.Ceiling(Math.Abs(diff) / _transactionsDecayPerTicker) + 1) * decayIntervalInMs;
                var waitTs = TimeSpan.FromMilliseconds((int)waitTimeInMs);
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "TransactionRateLimit",
                    $"The API request has been rate limited. To avoid this message, please reduce the frequency of API calls. Will wait {waitTs.TotalSeconds} seconds..."));

                _cancellationTokenSource.Token.WaitHandle.WaitOne(waitTs);
                retryCount++;
            }

            bool TryUpdateCounter(Symbol sym, int wgt, DateTime dateTime, out decimal difference)
            {
                lock (_transactionLocker)
                {
                    var (counter, lastUsed) = _transactionsCountersPerTicker.GetOrAdd(sym, _ => (0, DateTime.UnixEpoch));
                    var counterAfterDecay = Math.Max(counter - CalculatedDecaySinceLastUse(dateTime, lastUsed), 0);

                    var counterAfterOperation = counterAfterDecay + wgt;
                    difference = _transactionsLimitPerTicker - counterAfterOperation;

                    if (difference >= 0)
                    {
                        _transactionsCountersPerTicker[sym] = (counterAfterOperation, dateTime);
                        return true;
                    }
                    return false;
                }
            }

            decimal CalculatedDecaySinceLastUse(DateTime dateTime, DateTime lastUsed1)
            {
                var decayIntervals = (decimal)(dateTime - lastUsed1).TotalMilliseconds / decayIntervalInMs;
                var decay = decayIntervals * _transactionsDecayPerTicker;
                return decay;
            }
        }

        /// <summary>
        /// Kraken Add Order Rate limit check
        /// </summary>
        /// <param name="symbol">Brokerage symbol</param>
        public bool AddOrderRateLimitWaitToProceed(Symbol symbol)
        {
            try
            {
                TransactionLimitCheck(symbol, 1);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Kraken Cancel Order Rate limit check
        /// </summary>
        /// <param name="symbol">Brokerage symbol</param>
        /// <param name="time">Time order was placed</param>
        public bool CancelOrderRateLimitWaitToProceed(Symbol symbol, DateTime time)
        {
            try
            {
                var weight = GetRateLimitWeightCancelOrder(time);
                TransactionLimitCheck(symbol, weight);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private int GetRateLimitWeightCancelOrder(DateTime time)
        {
            var timePassed = DateTime.UtcNow - time;
            if (timePassed < TimeSpan.FromSeconds(5))
            {
                return 8;
            }

            if (timePassed < TimeSpan.FromSeconds(10))
            {
                return 6;
            }

            if (timePassed < TimeSpan.FromSeconds(15))
            {
                return 5;
            }

            if (timePassed < TimeSpan.FromSeconds(45))
            {
                return 4;
            }

            if (timePassed < TimeSpan.FromSeconds(90))
            {
                return 2;
            }

            if (timePassed < TimeSpan.FromSeconds(900))
            {
                return 1;
            }

            return 0;
        }

        private void OnMessage(BrokerageMessageEvent messageEvent)
        {
            Message?.Invoke(this, messageEvent);
        }

        /// <summary>
        /// Dispose kraken rateLimits
        /// </summary>
        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
        }
    }
}