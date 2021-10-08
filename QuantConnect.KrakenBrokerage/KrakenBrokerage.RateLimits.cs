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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using QuantConnect.Brokerages.Kraken.Models;
using QuantConnect.Logging;
using QuantConnect.Util;
using Timer = System.Timers.Timer;

namespace QuantConnect.Brokerages.Kraken
{
    /// <summary>
    /// Kraken rate limits implementation
    /// </summary>
    public class KrakenBrokerageRateLimits : IDisposable
    {
        /// <summary>
        /// Lockers to make code thread safe
        /// </summary>
        private readonly object RestLocker = new();
        private readonly object OrderLocker = new();
        private readonly object CancelLocker = new();
        public decimal RateLimitCounter { get; set; }
        
        // By default - Starter verification tier limits
        private readonly Dictionary<KrakenRateLimitType, decimal> _rateLimitsDictionary = new ()
        {
            {KrakenRateLimitType.Common, 15},
            {KrakenRateLimitType.Orders, 60},
            {KrakenRateLimitType.Cancel, 60}
        };

        private readonly Dictionary<KrakenRateLimitType, decimal> _rateLimitsDecayDictionary = new ()
        {
            {KrakenRateLimitType.Common, 0.33m},
            {KrakenRateLimitType.Cancel, 1m},
        };

        private readonly Timer _1sRateLimitTimer = new Timer(1000);
        
        private Dictionary<Symbol, int> RateLimitsOrderPerSymbolDictionary { get; set; }
        private Dictionary<string, decimal> RateLimitsCancelPerSymbolDictionary { get; set; }
        
        /// <summary>
        /// Choosing right rate limits based on verification tier
        /// </summary>
        /// <param name="verificationTier">Starter, Intermediate, Pro</param>
        public KrakenBrokerageRateLimits(string verificationTier)
        {
            RateLimitsOrderPerSymbolDictionary = new Dictionary<Symbol, int>();
            RateLimitsCancelPerSymbolDictionary = new Dictionary<string, decimal>();

            Enum.TryParse(verificationTier, true, out KrakenVerificationTier tier);
            
            switch (tier)
            {
                case KrakenVerificationTier.Intermediate:
                    _rateLimitsDictionary[KrakenRateLimitType.Common] = 20;
                    _rateLimitsDictionary[KrakenRateLimitType.Orders] = 80;
                    _rateLimitsDictionary[KrakenRateLimitType.Cancel] = 125;
                    _rateLimitsDecayDictionary[KrakenRateLimitType.Common] = 0.5m;
                    _rateLimitsDecayDictionary[KrakenRateLimitType.Cancel] = 2.34m;
                    break;
                case KrakenVerificationTier.Pro:
                    _rateLimitsDictionary[KrakenRateLimitType.Common] = 20;
                    _rateLimitsDictionary[KrakenRateLimitType.Orders] = 225;
                    _rateLimitsDictionary[KrakenRateLimitType.Cancel] = 180;
                    _rateLimitsDecayDictionary[KrakenRateLimitType.Common] = 1;
                    _rateLimitsDecayDictionary[KrakenRateLimitType.Cancel] = 3.75m;
                    break;
                default:
                    break;
            }
            
            _1sRateLimitTimer.Elapsed += DecaySpotRateLimits;
            _1sRateLimitTimer.Start();
        }

        /// <summary>
        /// Usual Rest request rate limit check
        /// </summary>
        public void RateLimitCheck()
        {
            bool isExceeded = false;
            lock (RestLocker)
            {
                isExceeded = RateLimitCounter + 1 > _rateLimitsDictionary[KrakenRateLimitType.Common];
            }

            if (isExceeded)
            {
                Log.Trace("Brokerage.OnMessage(): " + new BrokerageMessageEvent(BrokerageMessageType.Warning, "RestRateLimit",
                    "The API request has been rate limited. To avoid this message, please reduce the frequency of API calls."));
                Wait20Seconds();
            }

            lock (RestLocker)
            {
                RateLimitCounter++;
            }
            
        }

        /// <summary>
        /// Kraken order rate
        /// </summary>
        /// <param name="symbol">Brokerage symbol</param>
        /// <exception cref="BrokerageException"> Rate limit exceeded</exception>
        public void OrderRateLimitCheck(Symbol symbol)
        {
            var isExist = false;
            var currentOrdersCount = 0;
            lock (OrderLocker)
            {
                isExist = RateLimitsOrderPerSymbolDictionary.TryGetValue(symbol, out currentOrdersCount);
            }
            
            if (isExist)
            {
                if (currentOrdersCount >= _rateLimitsDictionary[KrakenRateLimitType.Orders])
                {
                    Log.Error("Brokerage.OnMessage(): " + new BrokerageMessageEvent(BrokerageMessageType.Error, "RateLimit",
                        $"Placing new orders of {symbol} symbol is not allowed. Your order limit: {_rateLimitsDictionary[KrakenRateLimitType.Orders]}, opened orders now: {currentOrdersCount}." +
                        $"Cancel orders to have ability to place new."));
                    throw new BrokerageException("Order placing limit is exceeded. Cancel open orders and then place new ones.");
                }

                lock (OrderLocker)
                {
                    RateLimitsOrderPerSymbolDictionary[symbol]++;
                }
            }
            else
            {
                lock (OrderLocker)
                {
                    RateLimitsOrderPerSymbolDictionary[symbol] = 1;
                }
            }
            
        }
        
        /// <summary>
        /// Kraken order rate decay
        /// </summary>
        /// <param name="symbol">Brokerage symbol</param>
        public void OrderRateLimitDecay(Symbol symbol)
        {
            lock (OrderLocker)
            {
                if (RateLimitsOrderPerSymbolDictionary.TryGetValue(symbol, out var currentOrdersCount))
                {
                    if (currentOrdersCount <= 0)
                    {
                        RateLimitsOrderPerSymbolDictionary[symbol] = 0;
                    }
                    else
                    {
                        RateLimitsOrderPerSymbolDictionary[symbol]--;
                    }
                }
            }
        }
        
        /// <summary>
        /// Kraken Cancel Order Rate limit check
        /// </summary>
        /// <param name="symbol">Brokerage symbol</param>
        /// <param name="time">Time order was placed</param>
        public void CancelOrderRateLimitCheck(string symbol, DateTime time)
        {
            var weight = GetRateLimitWeightCancelOrder(time);
            var isExist = false;
            var currentCancelOrderRate = 0m;
            
            lock (CancelLocker)
            {
                isExist = RateLimitsCancelPerSymbolDictionary.TryGetValue(symbol, out currentCancelOrderRate);
            }
            
            if (isExist)
            {
                if (currentCancelOrderRate + weight >= _rateLimitsDictionary[KrakenRateLimitType.Cancel])
                {
                    Log.Trace("Brokerage.OnMessage(): " + new BrokerageMessageEvent(BrokerageMessageType.Warning, "CancelRateLimit",
                        "The cancel order API request has been rate limited. To avoid this message, please reduce the frequency of cancel order API calls."));
                    Wait20Seconds();
                }

                lock (CancelLocker)
                {
                    RateLimitsCancelPerSymbolDictionary[symbol] += weight;
                }
            }
            else
            {
                lock (CancelLocker)
                {
                    RateLimitsCancelPerSymbolDictionary[symbol] = weight;
                }
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
        
        private void DecaySpotRateLimits(object o, ElapsedEventArgs agrs)
        {
            try
            {
                lock (RestLocker)
                {
                    if (RateLimitCounter <= _rateLimitsDecayDictionary[KrakenRateLimitType.Common])
                    {
                        RateLimitCounter = 0;
                    }
                    else
                    {
                        RateLimitCounter -= _rateLimitsDecayDictionary[KrakenRateLimitType.Common];
                    }
                }

                if (RateLimitsCancelPerSymbolDictionary.Count > 0)
                {
                    lock (CancelLocker)
                    {
                        foreach (var key in RateLimitsCancelPerSymbolDictionary.Keys)
                        {

                            if (RateLimitsCancelPerSymbolDictionary[key] <= _rateLimitsDecayDictionary[KrakenRateLimitType.Cancel])
                            {
                                RateLimitsCancelPerSymbolDictionary[key] = 0;
                            }
                            else
                            {
                                RateLimitsCancelPerSymbolDictionary[key] -= _rateLimitsDecayDictionary[KrakenRateLimitType.Cancel];
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private void Wait20Seconds()
        {
            var timer = new Timer(20000);
            timer.AutoReset = false;

            var manualSetEvent = new ManualResetEvent(false);

            timer.Elapsed += (o, e) => { manualSetEvent.Set(); };
            
            timer.Start();

            manualSetEvent.WaitOne();
            
            timer.Dispose();
            manualSetEvent.Dispose();
        }

        /// <summary>
        /// Dispose kraken rateLimits
        /// </summary>
        public void Dispose()
        {
            _1sRateLimitTimer?.Stop();
            _1sRateLimitTimer.DisposeSafely();
        }
    }
}