using System;
using System.Collections.Generic;
using System.Timers;
using QuantConnect.Brokerages.Kraken.Models;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Kraken
{
    /// <summary>
    /// Kraken rate limits implementation
    /// </summary>
    public class KrakenBrokerageRateLimits
    {
        public decimal RateLimitCounter { get; set; }
        
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
        
        private Dictionary<string, int> RateLimitsOrderPerSymbolDictionary { get; set; }
        private Dictionary<string, decimal> RateLimitsCancelPerSymbolDictionary { get; set; }
        
        // specify very big number of occurrences, because we will estimate it by ourselves. Will be used only for cooldown
        private readonly RateGate _restRateLimiter = new RateGate(1, TimeSpan.FromSeconds(20));
        
        /// <summary>
        /// Choosing right rate limits based on verification tier
        /// </summary>
        /// <param name="verificationTier">Starter, Intermediate, Pro</param>
        public KrakenBrokerageRateLimits(string verificationTier)
        {
            RateLimitsOrderPerSymbolDictionary = new Dictionary<string, int>();
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
            if (RateLimitCounter + 1 > _rateLimitsDictionary[KrakenRateLimitType.Common])
            {
                Log.Trace("Brokerage.OnMessage(): " + new BrokerageMessageEvent(BrokerageMessageType.Warning, "SpotRateLimit",
                    "The API request has been rate limited. To avoid this message, please reduce the frequency of API calls."));
                _restRateLimiter.WaitToProceed(TimeSpan.Zero);
                _restRateLimiter.WaitToProceed();
            }

            RateLimitCounter++;
        }

        /// <summary>
        /// Kraken order rate
        /// </summary>
        /// <param name="symbol">Brokerage symbol</param>
        /// <exception cref="BrokerageException"> Rate limit exceeded</exception>
        public void OrderRateLimitCheck(string symbol)
        {
            if (RateLimitsOrderPerSymbolDictionary.TryGetValue(symbol, out var currentOrdersCount))
            {
                if (currentOrdersCount >= _rateLimitsDictionary[KrakenRateLimitType.Orders])
                {
                    Log.Error("Brokerage.OnMessage(): " + new BrokerageMessageEvent(BrokerageMessageType.Error, "RateLimit",
                        $"Placing new orders of {symbol} symbol is not allowed. Your order limit: {_rateLimitsDictionary[KrakenRateLimitType.Orders]}, opened orders now: {currentOrdersCount}." +
                        $"Cancel orders to have ability to place new."));
                    throw new BrokerageException("Order placing limit is exceeded. Cancel open orders and then place new ones.");
                }

                RateLimitsOrderPerSymbolDictionary[symbol]++;
            }
            else
            {
                RateLimitsOrderPerSymbolDictionary[symbol] = 1;
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
            if (RateLimitsCancelPerSymbolDictionary.TryGetValue(symbol, out var currentCancelOrderRate))
            {
                if (currentCancelOrderRate + weight >= _rateLimitsDictionary[KrakenRateLimitType.Cancel])
                {
                    Log.Trace("Brokerage.OnMessage(): " + new BrokerageMessageEvent(BrokerageMessageType.Warning, "CancelRateLimit",
                        "The cancel order API request has been rate limited. To avoid this message, please reduce the frequency of cancel order API calls."));
                    _restRateLimiter.WaitToProceed(TimeSpan.Zero);
                    _restRateLimiter.WaitToProceed();
                }

                RateLimitsCancelPerSymbolDictionary[symbol] += weight;
            }
            else
            {
                RateLimitsCancelPerSymbolDictionary[symbol] = weight;
            }
        }

        private int GetRateLimitWeightCancelOrder(DateTime time)
        {
            var timeNow = DateTime.UtcNow;
            if (timeNow - time < TimeSpan.FromSeconds(5))
            {
                return 8;
            }

            if (timeNow - time < TimeSpan.FromSeconds(10))
            {
                return 6;
            }

            if (timeNow - time < TimeSpan.FromSeconds(15))
            {
                return 5;
            }

            if (timeNow - time < TimeSpan.FromSeconds(45))
            {
                return 4;
            }

            if (timeNow - time < TimeSpan.FromSeconds(90))
            {
                return 2;
            }

            if (timeNow - time < TimeSpan.FromSeconds(900))
            {
                return 1;
            }

            return 0;
        }
        
        private void DecaySpotRateLimits(object o, ElapsedEventArgs agrs)
        {
            if (RateLimitCounter <= _rateLimitsDecayDictionary[KrakenRateLimitType.Common])
            {
                RateLimitCounter = 0;
            }
            else
            {
                RateLimitCounter -= _rateLimitsDecayDictionary[KrakenRateLimitType.Common];
            }

            if (RateLimitsCancelPerSymbolDictionary.Count > 0)
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
}