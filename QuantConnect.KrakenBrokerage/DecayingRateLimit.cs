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
using System.Threading;

namespace QuantConnect.Brokerages.Kraken
{
    /// <summary>
    /// Implements a decaying rate limit strategy where the counter automatically decays over time.
    /// This allows for flexible rate limiting that decreases as time passes, similar to a token bucket
    /// with continuous refill.
    /// Thread-safe for concurrent access.
    /// </summary>
    public class DecayingRateLimit : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly int _decayIntervalInMs;
        private readonly int _limit;
        private readonly decimal _decayRate;
        private readonly CancellationToken _cancellationToken;

        private decimal _counter;
        private DateTime _lastUpdated;

        /// <summary>
        /// Event that fires when rate limit is exceeded and waiting is required
        /// </summary>
        public event EventHandler<BrokerageMessageEvent> Message;

        /// <summary>
        /// Creates a new decaying rate limit
        /// </summary>
        /// <param name="limit">Maximum allowed counter value</param>
        /// <param name="decayRate">Amount the counter decays per interval</param>
        /// <param name="decayIntervalInMs">Interval in milliseconds for decay calculation</param>
        /// <param name="cancellationToken">Cancellation token for waiting operations</param>
        public DecayingRateLimit(int limit, decimal decayRate, int decayIntervalInMs, CancellationToken cancellationToken)
        {
            _limit = limit;
            _decayRate = decayRate;
            _decayIntervalInMs = decayIntervalInMs;
            _cancellationToken = cancellationToken;
            _counter = 0;
            _lastUpdated = DateTime.UnixEpoch;
        }

        /// <summary>
        /// Attempts to acquire a rate limit slot with the specified weight.
        /// Will wait and retry if limit is exceeded.
        /// </summary>
        /// <param name="weight">Weight of the operation (cost in rate limit units)</param>
        /// <param name="identifier">Identifier for logging purposes</param>
        /// <returns>True if successful, false if cancelled</returns>
        public bool WaitToProceed(int weight, string identifier = "")
        {
            const int maxRetries = 10;
            var retryCount = 0;

            while (!TryAcquire(weight, DateTime.UtcNow, out var deficit))
            {
                if (retryCount >= maxRetries)
                {
                    throw new InvalidOperationException(
                        $"Failed to acquire rate limit slot for {identifier} after {maxRetries} retries. " +
                        "This may indicate excessive concurrent requests.");
                }

                var waitTimeInMs = (Math.Ceiling(Math.Abs(deficit) / _decayRate) + 1) * _decayIntervalInMs;
                var waitMs = (int)Math.Min(waitTimeInMs, int.MaxValue);
                var waitTs = TimeSpan.FromMilliseconds(waitMs);

                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "RateLimit",
                    $"The API request has been rate limited{(string.IsNullOrEmpty(identifier) ? "" : $" for {identifier}")}. " +
                    $"To avoid this message, please reduce the frequency of API calls. Will wait {waitTs.TotalSeconds} seconds..."));

                if (_cancellationToken.WaitHandle.WaitOne(waitTs))
                {
                    return false; // Cancelled
                }

                retryCount++;
            }

            return true;
        }

        /// <summary>
        /// Tries to acquire a rate limit slot without waiting
        /// </summary>
        /// <param name="weight">Weight of the operation</param>
        /// <param name="now">Current timestamp</param>
        /// <param name="deficit">Output parameter indicating how much over the limit (negative if over)</param>
        /// <returns>True if acquired successfully, false if limit exceeded</returns>
        private bool TryAcquire(int weight, DateTime now, out decimal deficit)
        {
            lock (_lock)
            {
                var counterAfterDecay = Math.Max(_counter - CalculateDecay(now, _lastUpdated), 0);
                var counterAfterOperation = counterAfterDecay + weight;
                deficit = _limit - counterAfterOperation;

                if (deficit >= 0)
                {
                    _counter = counterAfterOperation;
                    _lastUpdated = now;
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Calculates how much the counter should decay based on elapsed time
        /// </summary>
        private decimal CalculateDecay(DateTime now, DateTime lastUsed)
        {
            var decayIntervals = (decimal)(now - lastUsed).TotalMilliseconds / _decayIntervalInMs;
            return decayIntervals * _decayRate;
        }

        private void OnMessage(BrokerageMessageEvent messageEvent)
        {
            Message?.Invoke(this, messageEvent);
        }

        public void Dispose()
        {
            // Nothing to dispose in this implementation
        }
    }
}