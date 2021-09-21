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
using System.Linq;
using System.Net;
using System.Timers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.Kraken.Models;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;
using RestSharp;
using HistoryRequest = QuantConnect.Data.HistoryRequest;

namespace QuantConnect.Brokerages.Kraken
{
    /// <summary>
    /// Binance brokerage implementation
    /// </summary>
    [BrokerageFactory(typeof(KrakenBrokerageFactory))]
    public partial class KrakenBrokerage : BaseWebsocketsBrokerage, IDataQueueHandler
    {
        private readonly IAlgorithm _algorithm;
        private readonly ISecurityProvider _securityProvider;
        private readonly IDataAggregator _aggregator;
        private readonly KrakenSymbolMapper _symbolMapper = new KrakenSymbolMapper();
        private LiveNodePacket _job;

        private const string _apiUrl = "https://api.kraken.com";
        private const string _wsUrl = "wss://ws.kraken.com";
        private const string _wsAuthUrl = "wss://ws-auth.kraken.com";
        
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

        public decimal RateLimitCounter { get; set; }

        private readonly Timer _1sRateLimitTimer = new Timer(1000);
        
        private Dictionary<string, int> RateLimitsOrderPerSymbolDictionary { get; set; }
        private Dictionary<string, decimal> RateLimitsCancelPerSymbolDictionary { get; set; }
        
        // specify very big number of occurrences, because we will estimate it by ourselves. Will be used only for cooldown
        private readonly RateGate _restRateLimiter = new RateGate(150, TimeSpan.FromSeconds(45)); 
        
        private readonly RateGate _webSocketRateLimiter = new RateGate(1, TimeSpan.FromSeconds(5));
        
        /// <summary>
        /// Needed to catch placed orders in websocket, then moves to CachedOrderIDs.
        /// </summary>
        private Dictionary<int, Order> PlacedOrdersDictionary { get; set; }

        /// <summary>
        /// The api secret spot
        /// </summary>
        protected string ApiSecret;

        /// <summary>
        /// The api key spot
        /// </summary>
        /// 
        protected string ApiKey;


        /// <summary>
        /// Constructor for brokerage
        /// </summary>
        /// <param name="apiKey"></param>
        /// <param name="apiSecret"></param>
        /// <param name="verificationTier"></param>
        /// <param name="algorithm"></param>
        /// <param name="aggregator"></param>
        /// <param name="job"></param>
        public KrakenBrokerage(string apiKey, string apiSecret,  string verificationTier, IAlgorithm algorithm, IDataAggregator aggregator, LiveNodePacket job)
            : base(_wsAuthUrl, new KrakenWebSocketWrapper(null), new RestClient(_apiUrl), apiKey, apiSecret, "Kraken")
        {
            _algorithm = algorithm;
            _job = job;
            _aggregator = aggregator;
            _securityProvider = algorithm.Portfolio;
            
            ApiKey = apiKey;
            ApiSecret = apiSecret;

            RateLimitsOrderPerSymbolDictionary = new Dictionary<string, int>();
            RateLimitsCancelPerSymbolDictionary = new Dictionary<string, decimal>();
            PlacedOrdersDictionary = new Dictionary<int, Order>();

            switch (verificationTier.ToLower())
            {
                case "intermediate":
                    _rateLimitsDictionary[KrakenRateLimitType.Common] = 20;
                    _rateLimitsDictionary[KrakenRateLimitType.Orders] = 80;
                    _rateLimitsDictionary[KrakenRateLimitType.Cancel] = 125;
                    _rateLimitsDecayDictionary[KrakenRateLimitType.Common] = 0.5m;
                    _rateLimitsDecayDictionary[KrakenRateLimitType.Cancel] = 2.34m;
                    break;
                case "pro":
                    _rateLimitsDictionary[KrakenRateLimitType.Common] = 20;
                    _rateLimitsDictionary[KrakenRateLimitType.Orders] = 225;
                    _rateLimitsDictionary[KrakenRateLimitType.Cancel] = 180;
                    _rateLimitsDecayDictionary[KrakenRateLimitType.Common] = 1;
                    _rateLimitsDecayDictionary[KrakenRateLimitType.Cancel] = 3.75m;
                    break;
                default:
                    break;
            }
            
            SubscriptionManager = new BrokerageMultiWebSocketSubscriptionManager(
                _wsUrl,
                100,
                0,
                null,
                () => new KrakenWebSocketWrapper(null),
                Subscribe,
                Unsubscribe,
                OnDataMessage,
                TimeSpan.Zero, 
                _webSocketRateLimiter);
            
            
            _1sRateLimitTimer.Elapsed += DecaySpotRateLimits;
            _1sRateLimitTimer.Start();

            WebSocket.Open += (sender, args) => { SubscribeAuth(); };

        }

        /// <summary>
        /// Constructor for brokerage without configs
        /// </summary>
        /// <param name="algorithm"></param>
        /// <param name="aggregator"></param>
        /// <param name="job"></param>
        public KrakenBrokerage(IAlgorithm algorithm, IDataAggregator aggregator, LiveNodePacket job)
            :
            this(Config.Get("kraken-api-key"),
                Config.Get("kraken-api-secret"),
                Config.Get("kraken-verification-tier"),
                algorithm, aggregator, job)
        {
        }

        #region RateLimits
      
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
        

        private void RateLimitCheck()
        {
            if (RateLimitCounter + 1 > _rateLimitsDictionary[KrakenRateLimitType.Common])
            {
                Log.Trace("Brokerage.OnMessage(): " + new BrokerageMessageEvent(BrokerageMessageType.Warning, "SpotRateLimit",
                    "The API request has been rate limited. To avoid this message, please reduce the frequency of API calls."));
                _restRateLimiter.WaitToProceed();
            }

            RateLimitCounter++;
        }

        private void OrderRateLimitCheck(string symbol)
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
        }
        
        private void CancelOrderRateLimitCheck(string symbol, DateTime time)
        {
            var weight = GetRateLimitWeightCancelOrder(time);
            if (RateLimitsCancelPerSymbolDictionary.TryGetValue(symbol, out var currentCancelOrderRate))
            {
                if (currentCancelOrderRate + weight >= _rateLimitsDictionary[KrakenRateLimitType.Orders])
                {
                    Log.Trace("Brokerage.OnMessage(): " + new BrokerageMessageEvent(BrokerageMessageType.Warning, "CancelRateLimit",
                        "The cancel order API request has been rate limited. To avoid this message, please reduce the frequency of cancel order API calls."));
                    _restRateLimiter.WaitToProceed();
                }

                RateLimitsCancelPerSymbolDictionary[symbol] += weight;
                return;
            }

            RateLimitsCancelPerSymbolDictionary[symbol] = weight;
        }

        
        #endregion
        
        public override bool IsConnected => WebSocket.IsOpen;

        /// <summary>
        /// Gets all Kraken orders not yet closed
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public override List<Order> GetOpenOrders()
        {
            var nonce = Time.DateTimeToUnixTimeStampMilliseconds(DateTime.UtcNow).ConvertInvariant<long>();
            var param = new Dictionary<string, object>
            {
                {"nonce" , nonce.ToString()}
            };
            string query = "/0/private/OpenOrders";
            
            var headers = CreateSignature(query, nonce, BuildUrlEncode(param));
            
            var request = CreateRequest(query, headers, param, Method.POST);

            var response = ExecuteRestRequest(request);
            
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception($"KrakenBrokerage.GetOpenOrders: request failed: [{(int)response.StatusCode}] {response.StatusDescription}, Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
            }

            var token = JToken.Parse(response.Content);
            
            List<Order> list = new List<Order>();
            foreach (JProperty item in token["result"]["open"].Children())
            {
                var krakenOrder = item.Value.ToObject<KrakenOpenOrder>();
                Order order;
                var quantity = krakenOrder.Vol;
                var symbol = _symbolMapper.GetLeanSymbolFromOpenOrders(krakenOrder.Descr.Pair);
                var time = !string.IsNullOrEmpty(krakenOrder.Opentm) ? Time.UnixTimeStampToDateTime(krakenOrder.Opentm.ConvertInvariant<double>()) : DateTime.UtcNow;
                var brokerId = item.Name;
                
                var properties = new KrakenOrderProperties();

                if (krakenOrder.Oflags.Contains("post"))
                {
                    properties.PostOnly = true;
                }
                if (krakenOrder.Oflags.Contains("fcib"))
                {
                    properties.FeeInBase = true;
                }
                if (krakenOrder.Oflags.Contains("fciq"))
                {
                    properties.FeeInQuote = true;
                }
                if (krakenOrder.Oflags.Contains("nompp"))
                {
                    properties.NoMarketPriceProtection = true;
                }

                switch (krakenOrder.Descr.OrderType.LazyToUpper())
                {
                    case "MARKET":
                        order = new MarketOrder(symbol, quantity, time, properties: properties);
                        break;
                    case "LIMIT":
                        var limPrice = krakenOrder.Descr.Price;
                        order = new LimitOrder(symbol, quantity, limPrice, time, properties: properties)
                        {
                            BrokerId = {brokerId}
                        };
                        break;
                    case "STOP-LOSS":
                        var stopPrice = krakenOrder.Descr.Price;
                        order = new StopMarketOrder(symbol, quantity, stopPrice, time, properties: properties)               
                        {
                            BrokerId = {brokerId}
                        };
                        break;
                    case "TAKE-PROFIT":
                        var tpPrice = krakenOrder.Descr.Price;
                        order = new StopMarketOrder(symbol, quantity, tpPrice, time, properties: properties)
                        {
                            BrokerId = {brokerId}
                        };
                        break;
                    case "STOP-LOSS-LIMIT":
                        var stpPrice = krakenOrder.Descr.Price;
                        var limitPrice = krakenOrder.Descr.Price2;
                        order = new StopLimitOrder(symbol, quantity, stpPrice, limitPrice, time, properties: properties)
                        {
                            BrokerId = {brokerId}
                        };
                        break;
                    case "TAKE-PROFIT-LIMIT":
                        var takePrice = krakenOrder.Descr.Price;
                        var lmtPrice = krakenOrder.Descr.Price2;
                        order = new LimitIfTouchedOrder(symbol, quantity, takePrice, lmtPrice, time, properties: properties)
                        {
                            BrokerId = {brokerId}
                        };
                        break;
                    default:
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1,
                            "BinanceBrokerage.GetOpenOrders: Unsupported order type returned from brokerage: " + item.Type));
                        continue;
                }

                order.Status = GetOrderStatus(krakenOrder.Status);

                if (order.Status.IsOpen())
                {
                    var cached = CachedOrderIDs.Where(c => c.Value.BrokerId.Contains(order.BrokerId.First())).ToList();
                    if (cached.Any())
                    {
                        CachedOrderIDs[cached.First().Key] = order;
                    }
                }
            
                list.Add(order);
            }
            
            return list;
            
        }

        /// <summary>
        /// Get Kraken Holdings
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public override List<Holding> GetAccountHoldings()
        {
            var nonce = Time.DateTimeToUnixTimeStampMilliseconds(DateTime.UtcNow).ConvertInvariant<long>();
            var param = new Dictionary<string, object>
            {
                {"nonce" , nonce.ToString()},
                {"docalcs", true}
            };
            string query = "/0/private/OpenPositions";
            
            var headers = CreateSignature(query, nonce, BuildUrlEncode(param));
            
            var request = CreateRequest(query, headers, param, Method.POST);

            var response = ExecuteRestRequest(request);
            
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception($"KrakenBrokerage.GetAccountHoldings: request failed: [{(int)response.StatusCode}] {response.StatusDescription}, Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
            }

            var token = JToken.Parse(response.Content);

            var holdings = new List<Holding>();

            foreach (JProperty balance in token["result"].Children())
            {
                var krakenPosition = balance.Value.ToObject<KrakenOpenPosition>();
                var holding = new Holding
                {
                    Symbol = _symbolMapper.GetLeanSymbol(krakenPosition.Pair),
                    Quantity = krakenPosition.Vol,
                    UnrealizedPnL = krakenPosition.Net,
                    MarketValue = krakenPosition.Value,
                };

                holding.AveragePrice = krakenPosition.Cost / holding.Quantity;
                CurrencyPairUtil.DecomposeCurrencyPair(holding.Symbol, out _, out var quote);
                holding.CurrencySymbol = quote;
                if (krakenPosition.Type == "sell")
                {
                    holding.Quantity *= -1;
                }
                
                holdings.Add(holding);
            }

            return holdings;
            
        }
        
        /// <summary>
        /// Get Kraken Balances
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public override List<CashAmount> GetCashBalance()
        {
            var nonce = Time.DateTimeToUnixTimeStampMilliseconds(DateTime.UtcNow).ConvertInvariant<long>();
            var param = new Dictionary<string, object>
            {
                {"nonce" , nonce.ToString()}
            };
            string query = "/0/private/Balance";
            
            var headers = CreateSignature(query, nonce, BuildUrlEncode(param));

            var request = CreateRequest(query, headers, param, Method.POST);

            var response = ExecuteRestRequest(request);
            
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception($"KrakenBrokerage.GetCashBalance: request failed: [{(int)response.StatusCode}] {response.StatusDescription}, Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
            }

            var token = JToken.Parse(response.Content);

            var cash = new List<CashAmount>();

            var dictBalances = token["result"].ToObject<Dictionary<string, decimal>>();

            foreach (var balance in dictBalances)
            {
                cash.Add(new CashAmount(balance.Value, _symbolMapper.ConvertCurrency(balance.Key)));
            }

            return cash;
        }

        /// <summary>
        /// Place Kraken Order
        /// </summary>
        /// <param name="order"></param>
        /// <returns></returns>
        public override bool PlaceOrder(Order order)
        {
            var token = GetWebsocketToken();

            var parameters = CreateKrakenOrder(order, token, out var symbol);

            var json = JsonConvert.SerializeObject(parameters);

            OrderRateLimitCheck(symbol);
            
            WebSocket.Send(json);

            return true;
        }

        /// <summary>
        /// This operation is not supported
        /// </summary>
        /// <param name="order"></param>
        /// <returns></returns>
        public override bool UpdateOrder(Order order)
        {
            throw new NotSupportedException("KrakenBrokerage.UpdateOrder: Order update not supported. Please cancel and re-create.");
        }

        /// <summary>
        /// Cancels the order with the specified ID
        /// </summary>
        /// <param name="order">The order to cancel</param>
        /// <returns>True if the request was submitted for cancellation, false otherwise</returns>
        public override bool CancelOrder(Order order)
        {
            Log.Trace("KrakenBrokerage.CancelOrder(): {0}", order);

            if (!order.BrokerId.Any())
            {
                // we need the brokerage order id in order to perform a cancellation
                Log.Trace("KrakenBrokerage.CancelOrder(): Unable to cancel order without BrokerId.");
                return false;
            }
            var token = GetWebsocketToken();
            var json = JsonConvert.SerializeObject(new
            {
                @event = "cancelOrder",
                token,
                txid = order.BrokerId
            });
            
            CancelOrderRateLimitCheck(_symbolMapper.GetBrokerageSymbol(order.Symbol), order.CreatedTime);
            WebSocket.Send(json);

            return true;
        }
        

        /// <summary>
        /// Gets the history for the requested security
        /// </summary>
        /// <param name="request">The historical data request</param>
        /// <returns>An enumerable of bars covering the span specified in the request</returns>
        public override IEnumerable<BaseData> GetHistory(HistoryRequest request)
        {
            if (request.Resolution == Resolution.Tick || request.Resolution == Resolution.Second)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidResolution",
                    $"{request.Resolution} resolution is not supported, no history returned"));
                yield break;
            }

            if (request.TickType != TickType.Trade)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidTickType",
                    $"{request.TickType} tick type not supported, no history returned"));
                yield break;
            }

            if (request.Symbol.SecurityType != SecurityType.Crypto)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidSecurityType",
                    $"{request.Symbol.SecurityType} tick type not supported, no history returned. Use only {SecurityType.Crypto} type."));
                yield break;
            }

            var period = request.Resolution.ToTimeSpan();

            if (request.StartTimeUtc < DateTime.UtcNow - period * 720)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotFullHistoryWarning",
                    $"Kraken return max 720 TradeBars in history request. Now it will return TradeBars starting from {DateTime.UtcNow - period * 720}"));
            }
            
            var marketSymbol = _symbolMapper.GetBrokerageSymbol(request.Symbol);
            var resolution = ConvertResolution(request.Resolution);
            string url = _apiUrl + $"/0/public/OHLC?pair={marketSymbol}&interval={resolution}";
            var start = (long) Time.DateTimeToUnixTimeStamp(request.StartTimeUtc);
            var end = (long) Time.DateTimeToUnixTimeStamp(request.EndTimeUtc);
            var resolutionInMs = (long)request.Resolution.ToTimeSpan().TotalSeconds;

            while (end - start >= resolutionInMs)
            {
                var timeframe = $"&since={start}";

                var restRequest = CreateRequest(url + timeframe);
                
                var response = ExecuteRestRequest(restRequest);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception($"KrakenBrokerage.GetHistory: request failed: [{(int)response.StatusCode}] {response.StatusDescription}, Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
                }

                var token = JToken.Parse(response.Content);
                var candlesList = token["result"][marketSymbol].ToObject<List<KrakenCandle>>();
                
                if (candlesList.Count > 0)
                {
                    var lastValue = candlesList[^1];
                    if (Log.DebuggingEnabled)
                    {
                        var windowStartTime = Time.UnixTimeStampToDateTime(candlesList[0].Time);
                        var windowEndTime = Time.UnixTimeStampToDateTime(lastValue.Time + resolutionInMs);
                        Log.Debug($"KrakenRestApiClient.GetHistory(): Received [{marketSymbol}] data for time period from {windowStartTime.ToStringInvariant()} to {windowEndTime.ToStringInvariant()}..");
                    }
                    start = (long)lastValue.Time + resolutionInMs;

                    foreach (var kline in candlesList)
                    {
                        yield return new TradeBar()
                        {
                            Time = Time.UnixTimeStampToDateTime(kline.Time),
                            Symbol = request.Symbol,
                            Low = kline.Low,
                            High = kline.High,
                            Open = kline.Open,
                            Close = kline.Close,
                            Volume = kline.Volume,
                            Value = kline.Close,
                            DataType = MarketDataType.TradeBar,
                            Period = period
                        };
                    }
                }
                else
                {
                    // if there is no data just break
                    break;
                }
            }

        }
        

        /// <summary>
        /// If an IP address exceeds a certain number of requests per minute
        /// HTTP 429 return code is used when breaking a request rate limit.
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        private IRestResponse ExecuteRestRequest(IRestRequest request)
        {
            const int maxAttempts = 10;
            var attempts = 0;
            IRestResponse response;

            do
            {
                RateLimitCheck();
                
                response = RestClient.Execute(request);
                // 429 status code: Too Many Requests
            } while (++attempts < maxAttempts && (int)response.StatusCode == 429);

            return response;
        }

    }
}
