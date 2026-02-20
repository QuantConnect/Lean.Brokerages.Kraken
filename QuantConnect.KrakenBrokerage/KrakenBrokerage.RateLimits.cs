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
        private readonly int _openOrdersRateLimitPerTicker;

        private readonly Dictionary<KrakenVerificationTier, int> _openOrdersRateLimitsPerTicker = new()
        {
            [KrakenVerificationTier.Starter] = 60,
            [KrakenVerificationTier.Intermediate] = 80,
            [KrakenVerificationTier.Pro] = 225,
        };

        // REST API rate limit
        private readonly DecayingRateLimit _restApiRateLimit;

        // Transaction limits per symbol
        private const int TransactionsLimitPerTickerSafetyMargin = 10;
        private readonly ConcurrentDictionary<Symbol, DecayingRateLimit> _transactionRateLimitsPerSymbol = new();
        private readonly int _transactionsLimitPerTicker;
        private readonly decimal _transactionsDecayPerTicker;

        private readonly int _decayIntervalInMs;


        private readonly Dictionary<KrakenVerificationTier, int> _transactionsLimitsPerTicker = new()
        {
            [KrakenVerificationTier.Starter] = 60,
            [KrakenVerificationTier.Intermediate] = 125,
            [KrakenVerificationTier.Pro] = 180,
        };

        private readonly Dictionary<KrakenVerificationTier, decimal> _transactionsDecayLimitsPerTicker = new()
        {
            [KrakenVerificationTier.Starter] = 1m,
            [KrakenVerificationTier.Intermediate] = 2.34m,
            [KrakenVerificationTier.Pro] = 3.75m,
        };

        private readonly Dictionary<KrakenVerificationTier, int> _restLimitsPerTicker = new()
        {
            [KrakenVerificationTier.Starter] = 15,
            [KrakenVerificationTier.Intermediate] = 20,
            [KrakenVerificationTier.Pro] = 20,
        };

        private readonly Dictionary<KrakenVerificationTier, decimal> _restDecayLimitsPerTicker = new()
        {
            [KrakenVerificationTier.Starter] = 0.33m,
            [KrakenVerificationTier.Intermediate] = 0.5m,
            [KrakenVerificationTier.Pro] = 1m,
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
            _decayIntervalInMs = timerInterval;
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

            // Initialize REST API rate limit
            _restApiRateLimit = new DecayingRateLimit(
                _restLimitsPerTicker[tier],
                _restDecayLimitsPerTicker[tier],
                _decayIntervalInMs,
                _cancellationTokenSource.Token);
            _restApiRateLimit.Message += OnRateLimitMessage;
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
        /// Gets or creates a rate limit for a specific symbol
        /// </summary>
        private DecayingRateLimit GetOrCreateSymbolRateLimit(Symbol symbol)
        {
            return _transactionRateLimitsPerSymbol.GetOrAdd(symbol, _ =>
            {
                var rateLimit = new DecayingRateLimit(
                    _transactionsLimitPerTicker,
                    _transactionsDecayPerTicker,
                    _decayIntervalInMs,
                    _cancellationTokenSource.Token);
                rateLimit.Message += OnRateLimitMessage;
                return rateLimit;
            });
        }

        /// <summary>
        /// Kraken Add Order Rate limit check (per symbol)
        /// </summary>
        /// <param name="symbol">Brokerage symbol</param>
        public bool AddOrderRateLimitWaitToProceed(Symbol symbol)
        {
            try
            {
                var rateLimit = GetOrCreateSymbolRateLimit(symbol);
                return rateLimit.WaitToProceed(1, symbol.ToString());
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Kraken Cancel Order Rate limit check (per symbol)
        /// </summary>
        /// <param name="symbol">Brokerage symbol</param>
        /// <param name="time">Time order was placed</param>
        public bool CancelOrderRateLimitWaitToProceed(Symbol symbol, DateTime time)
        {
            try
            {
                var weight = GetRateLimitWeightCancelOrder(time);
                var rateLimit = GetOrCreateSymbolRateLimit(symbol);
                return rateLimit.WaitToProceed(weight, symbol.ToString());
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// REST API rate limit check (not per symbol)
        /// </summary>
        /// <param name="weight">Weight of the operation</param>
        /// <param name="identifier">Identifier for logging</param>
        public bool RestApiRateLimitWaitToProceed(int weight = 1, string identifier = "")
        {
            try
            {
                return _restApiRateLimit.WaitToProceed(weight, identifier);
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

        private void OnRateLimitMessage(object sender, BrokerageMessageEvent messageEvent)
        {
            Message?.Invoke(this, messageEvent);
        }

        /// <summary>
        /// Dispose kraken rateLimits
        /// </summary>
        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _restApiRateLimit?.Dispose();
            foreach (var rateLimit in _transactionRateLimitsPerSymbol.Values)
            {
                rateLimit?.Dispose();
            }
            _transactionRateLimitsPerSymbol.Clear();
        }
    }
}