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
using System.Security.Cryptography;
using System.Text;
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
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.TimeInForces;
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
    public class KrakenBrokerage : BaseWebsocketsBrokerage, IDataQueueHandler
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
        /// Locking object for the Ticks list in the data queue handler
        /// </summary>
        public readonly object TickLocker = new object();
        
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
            if (RateLimitsCancelPerSymbolDictionary.TryGetValue(symbol, out var currentCancelOrderRate))
            {
                var weight = GetRateLimitWeightCancelOrder(time);
                if (currentCancelOrderRate + weight >= _rateLimitsDictionary[KrakenRateLimitType.Orders])
                {
                    Log.Trace("Brokerage.OnMessage(): " + new BrokerageMessageEvent(BrokerageMessageType.Warning, "CancelRateLimit",
                        "The cancel order API request has been rate limited. To avoid this message, please reduce the frequency of cancel order API calls."));
                    _restRateLimiter.WaitToProceed();
                }

                RateLimitsCancelPerSymbolDictionary[symbol] += weight;
            }
        }

        private int GetRateLimitWeightCancelOrder(DateTime time)
        {
            var timeNow = DateTime.Now;
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


        #endregion
        

        public override bool IsConnected => WebSocket.IsOpen;
        
        
        public Dictionary<string, string> CreateSignature(string path, long nonce, string body = "")
        {
            Dictionary<string, string> header = new();
            var concat = nonce + body;
            var hash256 = new SHA256Managed();
            var hash = hash256.ComputeHash(Encoding.UTF8.GetBytes(concat));
            var secretDecoded = Convert.FromBase64String(ApiSecret);
            var hmacsha512 = new HMACSHA512(secretDecoded);

            var urlBytes = Encoding.UTF8.GetBytes(path);
            var buffer = new byte[urlBytes.Length + hash.Length];
            Buffer.BlockCopy(urlBytes, 0, buffer, 0, urlBytes.Length);
            Buffer.BlockCopy(hash, 0, buffer, urlBytes.Length, hash.Length);
            var hash2 = hmacsha512.ComputeHash(buffer);
            var finalKey = Convert.ToBase64String(hash2);

            header.Add("API-Key", ApiKey);
            header.Add("API-Sign", finalKey);
            return header;
        }
        
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
                Order order;
                var quantity = item.Value["vol"].ConvertInvariant<decimal>();
                var symbol = _symbolMapper.GetLeanSymbolFromOpenOrders(item.Value["descr"]["pair"].ToString());
                var timestamp = item.Value["opentm"].ConvertInvariant<double>();
                var time = timestamp != 0 ? Time.UnixTimeStampToDateTime(timestamp) : DateTime.UtcNow;
                var brokerId = item.Name;
                
                var properties = new KrakenOrderProperties();

                var flags = item.Value["oflags"].ToString();
                if (flags.Contains("post"))
                {
                    properties.PostOnly = true;
                }
                if (flags.Contains("fcib"))
                {
                    properties.FeeInBase = true;
                }
                if (flags.Contains("fciq"))
                {
                    properties.FeeInQuote = true;
                }
                if (flags.Contains("nompp"))
                {
                    properties.NoMarketPriceProtection = true;
                }

                switch (item.Value["descr"]["ordertype"].ToString().LazyToUpper())
                {
                    case "MARKET":
                        order = new MarketOrder(symbol, quantity, time, properties: properties);
                        break;
                    case "LIMIT":
                        var limPrice = item.Value["descr"]["price"].ConvertInvariant<decimal>();
                        order = new LimitOrder(symbol, quantity, limPrice, time, properties: properties)
                        {
                            BrokerId = {brokerId}
                        };
                        break;
                    case "STOP-LOSS":
                        var stopPrice = item.Value["descr"]["price"].ConvertInvariant<decimal>();
                        order = new StopMarketOrder(symbol, quantity, stopPrice, time, properties: properties)               
                        {
                            BrokerId = {brokerId}
                        };
                        break;
                    case "TAKE-PROFIT":
                        var tpPrice = item.Value["price"].ConvertInvariant<decimal>();
                        order = new StopMarketOrder(symbol, quantity, tpPrice, time, properties: properties)
                        {
                            BrokerId = {brokerId}
                        };
                        break;
                    case "STOP-LOSS-LIMIT":
                        var stpPrice = item.Value["descr"]["price"].ConvertInvariant<decimal>();
                        var limitPrice = item.Value["descr"]["price2"].ConvertInvariant<decimal>();
                        order = new StopLimitOrder(symbol, quantity, stpPrice, limitPrice, time, properties: properties)
                        {
                            BrokerId = {brokerId}
                        };
                        break;
                    case "TAKE-PROFIT-LIMIT":
                        var takePrice = item.Value["descr"]["price"].ConvertInvariant<decimal>();
                        var lmtPrice = item.Value["descr"]["price2"].ConvertInvariant<decimal>();
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

                order.Status = GetOrderStatus(item.Value["status"].ToString());

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
                var holding = new Holding
                {
                    Symbol = _symbolMapper.GetLeanSymbol(balance.Value["pair"].ToString()),
                    Quantity = balance.Value["vol"].ConvertInvariant<decimal>(),
                    UnrealizedPnL = balance.Value["net"].ConvertInvariant<decimal>(),
                    MarketValue = balance.Value["value"].ConvertInvariant<decimal>(),
                };

                holding.AveragePrice = balance.Value["cost"].ConvertInvariant<decimal>() / holding.Quantity;
                CurrencyPairUtil.DecomposeCurrencyPair(holding.Symbol, out _, out var quote);
                holding.CurrencySymbol = quote;
                if (balance.Value["type"].ToString() == "sell")
                {
                    holding.Quantity *= -1;
                }
                
                holdings.Add(holding);
            }

            return holdings;
            
        }

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

            foreach (JProperty balance in token["result"].Children())
            {
                cash.Add(new CashAmount(balance.Value.ConvertInvariant<decimal>(), _symbolMapper.ConvertCurrency(balance.Name)));
            }

            return cash;
        }

        public override bool PlaceOrder(Order order)
        {
            var token = GetWebsocketToken();

            var symbol = _symbolMapper.GetWebsocketSymbol(order.Symbol);

            var q = order.AbsoluteQuantity;
            var parameters = new JsonObject
            {
                {"event", "addOrder"},
                {"pair", symbol},
                {"volume", q.ToStringInvariant()},
                {"type", order.Direction == OrderDirection.Buy ? "buy" : "sell" },
                {"token", token},
            };

            var rand = new Random();

            int id = 0;
            do
            {
                id = rand.Next();
            } while (PlacedOrdersDictionary.ContainsKey(id));
            
            parameters.Add("reqid", id);
            PlacedOrdersDictionary[id] = order;
            
            if (order is MarketOrder)
            {
                parameters.Add("ordertype", "market");
            }
            else if (order is LimitOrder limitOrd)
            {
                parameters.Add("ordertype", "limit");
                parameters.Add("price", limitOrd.LimitPrice.ToStringInvariant());
            }
            else if (order is StopLimitOrder stopLimitOrder)
            {
                parameters.Add("ordertype", "stop-loss-limit");
                parameters.Add("price", stopLimitOrder.StopPrice.ToStringInvariant());
                parameters.Add("price2", stopLimitOrder.LimitPrice.ToStringInvariant());
            }
            else if (order is StopMarketOrder stopOrder)
            {
                parameters.Add("ordertype", "stop-loss");
                parameters.Add("price", stopOrder.StopPrice.ToStringInvariant());
            }
            else if (order is LimitIfTouchedOrder limitIfTouchedOrder)
            {
                parameters.Add("ordertype", "take-profit-limit");
                parameters.Add("price", limitIfTouchedOrder.TriggerPrice.ToStringInvariant());
                parameters.Add("price2", limitIfTouchedOrder.LimitPrice.ToStringInvariant());
            }

            
            if (order.Properties is KrakenOrderProperties krakenOrderProperties)
            {
                StringBuilder sb = new StringBuilder();
                if (krakenOrderProperties.PostOnly)
                {
                    sb.Append("post");
                }

                if (krakenOrderProperties.FeeInBase)
                {
                    sb.Append(",fcib");
                }
                
                if (krakenOrderProperties.FeeInQuote)
                {
                    sb.Append(",fciq");
                }
                
                if (krakenOrderProperties.NoMarketPriceProtection)
                {
                    sb.Append(",nompp");
                }

                if (sb.Length != 0)
                {
                    parameters.Add("oflags", sb.ToString());
                }

                if (krakenOrderProperties.ConditionalOrder != null)
                {
                    if (krakenOrderProperties.ConditionalOrder is MarketOrder)
                    {
                        throw new BrokerageException($"KrakenBrokerage.PlaceOrder: Conditional order type can't be Market. Specify other order type");
                    }
                    else if (krakenOrderProperties.ConditionalOrder is LimitOrder limitOrd)
                    {
                        parameters.Add("close[ordertype]", "limit");
                        parameters.Add("close[price]", limitOrd.LimitPrice.ToStringInvariant());
                    }
                    else if (krakenOrderProperties.ConditionalOrder is StopLimitOrder stopLimitOrder)
                    {
                        parameters.Add("close[ordertype]", "stop-loss-limit");
                        parameters.Add("close[price]", stopLimitOrder.StopPrice.ToStringInvariant());
                        parameters.Add("close[price2]", stopLimitOrder.LimitPrice.ToStringInvariant());
                    }
                    else if (krakenOrderProperties.ConditionalOrder is StopMarketOrder stopOrder)
                    {
                        parameters.Add("close[ordertype]", "stop-loss");
                        parameters.Add("close[price]", stopOrder.StopPrice.ToStringInvariant());
                    }
                    else if (krakenOrderProperties.ConditionalOrder is LimitIfTouchedOrder limitIfTouchedOrder)
                    {
                        parameters.Add("close[ordertype]", "take-profit-limit");
                        parameters.Add("close[price]", limitIfTouchedOrder.TriggerPrice.ToStringInvariant());
                        parameters.Add("close[price2]", limitIfTouchedOrder.LimitPrice.ToStringInvariant());
                    }
                }
            }

            try
            {
                var security = _securityProvider.GetSecurity(order.Symbol);

                var leverage = security.BuyingPowerModel.GetLeverage(security);
                if (leverage > 1)
                {
                    parameters.Add("leverage", leverage.ToStringInvariant());
                }
            }
            catch (Exception e)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, 1, $"Got error when specifying leverage. Default leverage set. Error: {e.Message}"));
            }

            var tif = order.TimeInForce;

            if (tif is GoodTilCanceledTimeInForce)
            {
                parameters.Add("timeinforce", "GTC");
            }
            else if (tif is GoodTilDateTimeInForce gtd)
            {
                parameters.Add("timeinforce", "GTD");
                parameters.Add("expiretm", Time.DateTimeToUnixTimeStamp(gtd.Expiry).ConvertInvariant<long>());
            }
            
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
            WebSocket.Send(json);

            return true;
        }

        #region IDataQueueHandler
        
        /// <summary>
        /// Get websocket token. Needs when subscribing to private feeds 
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private string GetWebsocketToken()
        {
            var nonce = Time.DateTimeToUnixTimeStampMilliseconds(DateTime.UtcNow).ConvertInvariant<long>();
            var param = new Dictionary<string, object>
            {
                {"nonce" , nonce.ToString()}
            };
            string query = "/0/private/GetWebSocketsToken";
            
            var headers = CreateSignature(query, nonce, BuildUrlEncode(param));

            var request = CreateRequest(query, headers, param, Method.POST);

            var response = ExecuteRestRequest(request);
            
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception($"KrakenBrokerage.GetCashBalance: request failed: [{(int)response.StatusCode}] {response.StatusDescription}, Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
            }

            var token = JToken.Parse(response.Content);

            return token["result"]["token"].ToString();
        }

        /// <summary>
        /// Sets the job we're subscribing for
        /// </summary>
        /// <param name="job">Job we're subscribing for</param>
        public void SetJob(LiveNodePacket job)
        {
        }

        private void SubscribeAuth()
        {
            if (WebSocket.IsOpen)
            {
                var token = GetWebsocketToken();

                WebSocket.Send(JsonConvert.SerializeObject(new
                {
                    @event = "subscribe",
                    subscription = new
                    {
                        token,
                        name = "openOrders"
                    }
                }));
            }
        }
        
        /// <summary>
        /// Closes the websockets connection
        /// </summary>
        public override void Disconnect()
        {
            WebSocket.Close();
            _webSocketRateLimiter.DisposeSafely();
        }

        /// <summary>
        /// Private message parser
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        /// <exception cref="NotImplementedException"></exception>
        public override void OnMessage(object sender, WebSocketMessage e)
        {
            var data = (WebSocketClientWrapper.TextMessage)e.Data;

            try
            {
                var token = JToken.Parse(data.Message);
                
                if (token is JArray array)
                {

                    if (array[1].ToString() == "openOrders")
                    {
                        EmitOrderEvent(array.First);
                        return;
                    }

                }
                else if (token is JObject)
                {

                    if (token["event"].ToString() == "heartbeat")
                    {
                        return;
                    }
                    
                    if (token["event"].ToString() == "systemStatus")
                    {
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Information, 200, $"KrakenWS system status: {token["status"]}"));
                        return;
                    }
                    
                    if (token["event"].ToString() == "addOrderStatus" && token["status"].ToString() != "error")
                    {
                        var userref = token["reqid"].ConvertInvariant<int>();
                        var brokerId = token["txid"].ToString();
                        PlacedOrdersDictionary.TryGetValue(userref, out var order);

                        if (order == null)
                        {
                            return; // order placed not by Lean - skip
                        }
                        
                        if (CachedOrderIDs.ContainsKey(order.Id))
                        {
                            CachedOrderIDs[order.Id].BrokerId.Clear();
                            CachedOrderIDs[order.Id].BrokerId.Add(brokerId);
                        }
                        else
                        {
                            order.BrokerId.Add(brokerId);
                            CachedOrderIDs.TryAdd(order.Id, order);
                        }

                        PlacedOrdersDictionary.Remove(userref);
                        return;
                    }

                    if (token["event"].ToString() == "cancelOrderStatus" && token["status"].ToString() != "error")
                    {
                        return;
                    }

                    if (token["status"].ToString() == "error")
                    {
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, $"Error {token["event"]} event. Message: {token["errorMessage"]}"));
                        return;
                    }
                    
                    if (token["status"].ToString() == "subscribed")
                    {
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Information, 200, $"Subscribing to authenticated channel {token["channelName"]}"));
                        return;
                    }
                    
                }
                
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, 10, $"Unable to parse authenticated websocket message. Don't have parser for this message: {data.Message}."));
            }
            catch (Exception exception)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, $"Parsing wss message failed. Data: {data.Message} Exception: {exception}"));
                throw;
            }
        }
        
        private void EmitOrderEvent(JToken update)
        {
            foreach (var trade in update.Children())
            {
                try
                {
                    var data = trade.First as JProperty;
                    var brokerId = data.Name;

                    var order = CachedOrderIDs
                        .FirstOrDefault(o => o.Value.BrokerId.Contains(brokerId))
                        .Value;

                    if (order == null)
                    {
                        order = _algorithm.Transactions.GetOrderByBrokerageId(brokerId);
                        if (order == null)
                        {
                            Log.Error($"OnOrderUpdate(): order not found: BrokerId: {brokerId}");
                            continue;
                        }
                    }

                    var updTime = DateTime.UtcNow;
                    CurrencyPairUtil.DecomposeCurrencyPair(order.Symbol, out var @base, out var quote);
                    OrderEvent orderEvent = null;
                    
                    var status = OrderStatus.New;

                    var feeCurrency = quote;
                    if (order.Properties is KrakenOrderProperties krakenOrderProperties)
                    {
                        if (krakenOrderProperties.FeeInBase)
                        {
                            feeCurrency = @base;
                        }
                    }
                    
                    if (!data.Value.ToString().Contains("vol_exec")) // status update
                    {
                        if (data.Value.ToString().Contains("status"))
                        {
                            status = GetOrderStatus(data.Value["status"].ToString());
                        }
                        else if (data.Value.ToString().Contains("touched"))
                        {
                            status = OrderStatus.UpdateSubmitted;
                        }

                        orderEvent = new OrderEvent
                        (
                            order.Id, order.Symbol, updTime, status,
                            order.Direction, 0, 0,
                            new OrderFee(new CashAmount(0m, feeCurrency)), $"Kraken Order Event {order.Direction}"
                        );
                    }
                    else
                    {
                        var fillPrice = data.Value["price"].ConvertInvariant<decimal>();
                        var fillQuantity = data.Value["vol_exec"].ConvertInvariant<decimal>();
                        var orderFee = new OrderFee(new CashAmount(data.Value["fee"].ConvertInvariant<decimal>(), feeCurrency));
                        OrderDirection direction;
                    
                        if (!data.Value.ToString().Contains("descr"))
                        {
                            if (!data.Value.ToString().Contains("status") && fillQuantity >= order.AbsoluteQuantity)
                            {
                                status = OrderStatus.Filled;
                            }
                            else
                            {
                                status = GetOrderStatus(data.Value["status"].ToString());
                                var timestamp = data.Value["lastupdated"].ConvertInvariant<double>();
                                updTime = timestamp != 0 ? Time.UnixTimeStampToDateTime(timestamp) : DateTime.UtcNow;
                            }

                            direction = order.Direction;
                            fillPrice = data.Value["avg_price"].ConvertInvariant<decimal>();
                        }
                        else
                        {
                            status = GetOrderStatus(data.Value["status"].ToString());
                        
                            direction = data.Value["descr"]["type"].ToString() == "sell" ? OrderDirection.Sell : OrderDirection.Buy;
                        }

                        if (fillQuantity != 0 && status != OrderStatus.Filled)
                        {
                            status = OrderStatus.PartiallyFilled;
                        }

                        orderEvent = new OrderEvent
                        (
                            order.Id, order.Symbol, updTime, status,
                            direction, fillPrice, fillQuantity,
                            orderFee, $"Kraken Order Event {direction}"
                        );
                    }
                    
                    // if the order is closed, we no longer need it in the active order list
                    if (status is OrderStatus.Filled or OrderStatus.Canceled )
                    {
                        Order outOrder;
                        CachedOrderIDs.TryRemove(order.Id, out outOrder);
                    }

                    OnOrderEvent(orderEvent);
                }
                catch (Exception e)
                {
                    Log.Error(e);
                    throw;
                }
            }
        }

        private OrderStatus GetOrderStatus(string status) => status switch
        {
            "pending" => OrderStatus.New,
            "open" => OrderStatus.Submitted,
            "closed" => OrderStatus.Filled,
            "expired" => OrderStatus.Canceled,
            "canceled" => OrderStatus.Canceled,
            _ => OrderStatus.None
        };

        /// <summary>
        /// Should be empty as handled in <see cref="BrokerageMultiWebSocketSubscriptionManager"/>
        /// </summary>
        /// <param name="symbols"></param>
        public override void Subscribe(IEnumerable<Symbol> symbols)
        {
            
        }

        #endregion
        


        #region Aggregator Update
        
        /// <summary>
        /// Subscribe to the specified configuration
        /// </summary>
        /// <param name="dataConfig">defines the parameters to subscribe to a data feed</param>
        /// <param name="newDataAvailableHandler">handler to be fired on new data available</param>
        /// <returns>The new enumerator for this subscription request</returns>
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            var symbol = dataConfig.Symbol;
            if (symbol.Value.Contains("UNIVERSE") ||
                !_symbolMapper.IsKnownLeanSymbol(symbol))
            {
                return Enumerable.Empty<BaseData>().GetEnumerator();
            }

            var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
            SubscriptionManager.Subscribe(dataConfig);

            return enumerator;
        }

        /// <summary>
        /// Removes the specified configuration
        /// </summary>
        /// <param name="dataConfig">Subscription config to be removed</param>
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            SubscriptionManager.Unsubscribe(dataConfig);
            _aggregator.Remove(dataConfig);
        }
        
        /// <summary>
        /// Subscribes to the requested symbol (using an individual streaming channel)
        /// </summary>
        /// <param name="webSocket">The websocket instance</param>
        /// <param name="symbol">The symbol to subscribe</param>
        private bool Subscribe(IWebSocket webSocket, Symbol symbol)
        { 
            _webSocketRateLimiter.WaitToProceed();
            webSocket.Send(JsonConvert.SerializeObject(new
            {
                @event = "subscribe",
                pair = new[]
                {
                    $"{_symbolMapper.GetWebsocketSymbol(symbol)}"
                },
                subscription = new
                {
                    name = "spread"
                }
            }));
            
            _webSocketRateLimiter.WaitToProceed();
            webSocket.Send(JsonConvert.SerializeObject(new
            {
                @event = "subscribe",
                pair = new[]
                {
                    $"{_symbolMapper.GetWebsocketSymbol(symbol)}"
                },
                subscription = new
                {
                    name = "trade"
                }
            }));

            return true;
        }

        /// <summary>
        /// Ends current subscription
        /// </summary>
        /// <param name="webSocket">The websocket instance</param>
        /// <param name="symbol">The symbol to unsubscribe</param>
        private bool Unsubscribe(IWebSocket webSocket, Symbol symbol)
        {

            webSocket.Send(JsonConvert.SerializeObject(new
            {
                @event = "unsubscribe",
                pair = new[]
                {
                    $"{_symbolMapper.GetWebsocketSymbol(symbol)}"
                },
                subscription = new
                {
                    name = "spread"
                }
            }));
            
            webSocket.Send(JsonConvert.SerializeObject(new
            {
                @event = "unsubscribe",
                pair = new[]
                {
                    $"{_symbolMapper.GetWebsocketSymbol(symbol)}"
                },
                subscription = new
                {
                    name = "trade"
                }
            }));

            return true;
        }
        
        private void OnDataMessage(WebSocketMessage webSocketMessage)
        {
            var e = (WebSocketClientWrapper.TextMessage)webSocketMessage.Data;
            var token = JToken.Parse(e.Message);

            if (token is JObject)
            {
                switch (token["event"].ToString())
                {
                    case "pong":
                    case "heartbeat":
                        return;
                    case "subscriptionStatus":
                        Log.Trace($"KrakenBrokerage.On{token["status"]}(): Channel subscribed: Id:{token["channelID"]} {token["pair"]}/{token["channelName"]}");
                        break;
                }
            }
            else if (token is JArray)
            {
                switch (token[2].ToString())
                {
                    case "spread":
                        JArray data = token[1] as JArray;
                        EmitQuoteTick(_symbolMapper.GetSymbolFromWebsocket(token[3].ToString()), Time.UnixTimeStampToDateTime(data[2].ConvertInvariant<double>()),
                            data[0].ConvertInvariant<decimal>(), data[3].ConvertInvariant<decimal>(), 
                            data[1].ConvertInvariant<decimal>(), data[4].ConvertInvariant<decimal>());
                        break;
                    case "trade":
                        ParseTradeMessage(token[1] as JArray, token[3].ToString());
                        break;
                }
            }
        }

        private void ParseTradeMessage(JArray trades, string symbol)
        {
            foreach (JArray data in trades)
            {
                EmitTradeTick(_symbolMapper.GetSymbolFromWebsocket(symbol), Time.UnixTimeStampToDateTime(data[2].ConvertInvariant<double>()), 
                    data[0].ConvertInvariant<decimal>(), data[1].ConvertInvariant<decimal>());
            }
        }
        
        private void EmitTradeTick(Symbol symbol, DateTime time, decimal price, decimal amount)
        {
            try
            {
                lock (TickLocker)
                {
                    EmitTick(new Tick
                    {
                        Value = price,
                        Time = time,
                        Symbol = symbol,
                        TickType = TickType.Trade,
                        Quantity = Math.Abs(amount),
                        Exchange = "kraken"
                    });
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
                throw;
            }
        }

        private void EmitQuoteTick(Symbol symbol, DateTime time, decimal bidPrice, decimal bidSize, decimal askPrice, decimal askSize)
        {
            lock (TickLocker)
            {
                EmitTick(new Tick
                {
                    AskPrice = askPrice,
                    BidPrice = bidPrice,
                    Value = (askPrice + bidPrice) / 2m,
                    Time = time,
                    Symbol = symbol,
                    TickType = TickType.Quote,
                    AskSize = Math.Abs(askSize),
                    BidSize = Math.Abs(bidSize), 
                    Exchange = "kraken"
                });
            }
        }

        /// <summary>
        /// Emit stream tick
        /// </summary>
        /// <param name="tick"></param>
        public void EmitTick(Tick tick)
        {
            _aggregator.Update(tick);
        }
        
        #endregion
        
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
                var ch = token["result"].Children().Children().First() as JArray;

                var candlesList = JsonConvert.DeserializeObject<object[][]>(ch.ToString()).ToList();
                
                if (candlesList.Count > 0)
                {
                    var lastValue = candlesList[^1];
                    if (Log.DebuggingEnabled)
                    {
                        var windowStartTime = Time.UnixMillisecondTimeStampToDateTime((long)candlesList[0][0]);
                        var windowEndTime = Time.UnixMillisecondTimeStampToDateTime((long)lastValue[0] + resolutionInMs);
                        Log.Debug($"KrakenRestApiClient.GetHistory(): Received [{marketSymbol}] data for time period from {windowStartTime.ToStringInvariant()} to {windowEndTime.ToStringInvariant()}..");
                    }
                    start = (long)lastValue[0] + resolutionInMs;

                    foreach (var kline in candlesList)
                    {
                        yield return new TradeBar()
                        {
                            Time = Time.UnixTimeStampToDateTime((long)kline[0]),
                            Symbol = request.Symbol,
                            Low = ((string)kline[3]).ConvertInvariant<decimal>(),
                            High = ((string)kline[2]).ConvertInvariant<decimal>(),
                            Open = ((string)kline[1]).ConvertInvariant<decimal>(),
                            Close = ((string)kline[4]).ConvertInvariant<decimal>(),
                            Volume = ((string)kline[6]).ConvertInvariant<decimal>(),
                            Value = ((string)kline[4]).ConvertInvariant<decimal>(),
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

        public Tick GetTick(Symbol symbol)
        {
            var marketSymbol = _symbolMapper.GetBrokerageSymbol(symbol);

            var restRequest = CreateRequest($"/0/public/Ticker?pair={marketSymbol}");

            var response = ExecuteRestRequest(restRequest);
            
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception($"KrakenBrokerage.GetTick: request failed: [{(int)response.StatusCode}] {response.StatusDescription}, Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
            }

            var token = JToken.Parse(response.Content);

            var element = token["result"].First as JProperty;

            var tick = new Tick
            {
                AskPrice = element.Value["a"][0].ConvertInvariant<decimal>(),
                BidPrice = element.Value["b"][0].ConvertInvariant<decimal>(),
                Value = element.Value["c"][0].ConvertInvariant<decimal>(),
                Time = DateTime.UtcNow,
                Symbol = symbol,
                TickType = TickType.Quote,
                AskSize = element.Value["a"][2].ConvertInvariant<decimal>(),
                BidSize = element.Value["b"][2].ConvertInvariant<decimal>(),
                Exchange = "kraken",
            };
            
            return tick;
        }
        
        private static string BuildUrlEncode(IDictionary<string, object> args) => string.Join(
            "&",
            args.Where(x => x.Value != null).Select(x => x.Key + "=" + x.Value)
        );
        
        private IRestRequest CreateRequest(string query, Dictionary<string, string> headers = null, IDictionary<string, object> requestBody = null, Method method = Method.GET)
        {
            RestRequest request = new RestRequest(query) { Method = method };

            if (headers is {Count: > 0})
            {
                request.AddHeaders(headers);
            }
            
            if (requestBody != null)
            {
                request.AddHeader("Content-Type", "application/x-www-form-urlencoded");

                string urlEncoded = BuildUrlEncode(requestBody);

                request.AddParameter("application/x-www-form-urlencoded", urlEncoded, ParameterType.RequestBody);
            }

            return request;
        }
        
        private string ConvertResolution(Resolution res) => res switch
        {
            Resolution.Hour => "60",
            Resolution.Daily => "1440",
            _ => "1"
        };

        /// <summary>
        /// If an IP address exceeds a certain number of requests per minute
        /// HTTP 429 return code is used when breaking a request rate limit.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="isOrderRequest">for order requests applies other rate limit checks</param>
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
