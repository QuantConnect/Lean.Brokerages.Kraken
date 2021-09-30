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
    /// Kraken brokerage implementation
    /// </summary>
    [BrokerageFactory(typeof(KrakenBrokerageFactory))]
    public partial class KrakenBrokerage : BaseWebsocketsBrokerage, IDataQueueHandler
    {
        private readonly IAlgorithm _algorithm;
        private readonly ISecurityProvider _securityProvider;
        private readonly IDataAggregator _aggregator;
        private readonly KrakenSymbolMapper _symbolMapper = new KrakenSymbolMapper();
        private LiveNodePacket _job;
        private readonly KrakenBrokerageRateLimits _rateLimiter;

        private const string _apiUrl = "https://api.kraken.com";
        private const string _wsUrl = "wss://ws.kraken.com";
        private const string _wsAuthUrl = "wss://ws-auth.kraken.com";

        private readonly RateGate _webSocketRateLimiter = new RateGate(1, TimeSpan.FromSeconds(5));

        /// <summary>
        /// Constructor for brokerage
        /// </summary>
        /// <param name="apiKey">Api key</param>
        /// <param name="apiSecret">Api secret</param>
        /// <param name="verificationTier">Account verification tier</param>
        /// <param name="algorithm"><see cref="IAlgorithm"/> instance</param>
        /// <param name="aggregator"><see cref="IDataAggregator"/> instance</param>
        /// <param name="job">Lean <see cref="LiveNodePacket"/></param>
        public KrakenBrokerage(string apiKey, string apiSecret,  string verificationTier, IAlgorithm algorithm, IDataAggregator aggregator, LiveNodePacket job)
            : base(_wsAuthUrl, new KrakenWebSocketWrapper(null), new RestClient(_apiUrl), apiKey, apiSecret, "Kraken")
        {
            _algorithm = algorithm;
            _job = job;
            _aggregator = aggregator;
            _securityProvider = algorithm?.Portfolio;

            _rateLimiter = new KrakenBrokerageRateLimits(verificationTier);

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
            
            WebSocket.Open += (sender, args) => { SubscribeAuth(); };

        }

        /// <summary>
        /// Constructor for brokerage without configs
        /// </summary>
        /// <param name="algorithm"><see cref="IAlgorithm"/> instance</param>
        /// <param name="aggregator"><see cref="IDataAggregator"/> instance</param>
        /// <param name="job">Lean <see cref="LiveNodePacket"/></param>
        public KrakenBrokerage(IAlgorithm algorithm, IDataAggregator aggregator, LiveNodePacket job)
            :
            this(Config.Get("kraken-api-key"),
                Config.Get("kraken-api-secret"),
                Config.Get("kraken-verification-tier"),
                algorithm, aggregator, job)
        {
        }
        
        
        public override bool IsConnected => WebSocket.IsOpen;

        /// <summary>
        /// Gets all Kraken orders not yet closed
        /// </summary>
        /// <returns>list of <see cref="Order"/></returns>
        /// <exception cref="Exception">Kraken api exception</exception>
        public override List<Order> GetOpenOrders()
        {
            string query = "/0/private/OpenOrders";
            
            var response = MakeRequest(query, method:Method.POST, isPrivate: true);
            
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
        /// <returns>list of <see cref="Holding"/></returns>
        /// <exception cref="Exception">Kraken api exception</exception>
        public override List<Holding> GetAccountHoldings()
        {
            if (_algorithm.BrokerageModel.AccountType == AccountType.Cash)
            {
                return base.GetAccountHoldings(_job?.BrokerageData, _algorithm.Securities.Values);
            }
            
            var param = new Dictionary<string, object>
            {
                {"docalcs", true}
            };
            var query = "/0/private/OpenPositions";

            var response = MakeRequest(query, param, Method.POST, true);
            
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
        /// <returns>list of <see cref="CashAmount"/></returns>
        /// <exception cref="Exception">Kraken api exception</exception>
        public override List<CashAmount> GetCashBalance()
        {
            string query = "/0/private/Balance";
            
            var response = MakeRequest(query,  method:Method.POST, isPrivate: true);
            
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
        /// <param name="order"><see cref="Order"/> to place</param>
        /// <returns>Order placed or not</returns>
        public override bool PlaceOrder(Order order)
        {
            WebsocketToken = string.IsNullOrEmpty(WebsocketToken) ? GetWebsocketToken() : WebsocketToken;

            var parameters = CreateKrakenOrder(order, out var symbol);

            var json = JsonConvert.SerializeObject(parameters);

            _rateLimiter.OrderRateLimitCheck(symbol);
            
            WebSocket.Send(json);

            return true;
        }

        /// <summary>
        /// This operation is not supported
        /// </summary>
        /// <param name="order"><see cref="Order"/> to update</param>
        /// <returns>Order updated or not</returns>
        public override bool UpdateOrder(Order order)
        {
            throw new NotSupportedException("KrakenBrokerage.UpdateOrder: Order update not supported. Please cancel and re-create.");
        }

        /// <summary>
        /// Cancels the order with the specified ID
        /// </summary>
        /// <param name="order"><see cref="Order"/> to cancel</param>
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
            
            WebsocketToken = string.IsNullOrEmpty(WebsocketToken) ? GetWebsocketToken() : WebsocketToken;
            var json = JsonConvert.SerializeObject(new
            {
                @event = "cancelOrder",
                token = WebsocketToken,
                txid = order.BrokerId
            });
            
            _rateLimiter.CancelOrderRateLimitCheck(_symbolMapper.GetBrokerageSymbol(order.Symbol), order.CreatedTime);
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
            if (request.Resolution == Resolution.Second)
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

            if (request.Resolution != Resolution.Tick && request.StartTimeUtc < DateTime.UtcNow - period * 720)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotFullHistoryWarning",
                    $"Kraken return max 720 TradeBars in history request. Now it will return TradeBars starting from {DateTime.UtcNow - period * 720}"));
            }
            
            var marketSymbol = _symbolMapper.GetBrokerageSymbol(request.Symbol);

            var enumerator = request.Resolution == Resolution.Tick ? GetTradeBars(request, marketSymbol) : GetOhlcBars(request, marketSymbol, period);
            foreach (var baseData in enumerator)
            {
                yield return baseData;
            }
        }

        /// <summary>
        /// Method to retrieve minute+ resolution TradeBars
        /// </summary>
        /// <param name="request"><see cref="HistoryRequest"/></param>
        /// <param name="marketSymbol">Symbol name like in brokerage</param>
        /// <param name="period">period for <see cref="TradeBar"/></param>
        /// <returns>List of <see cref="TradeBar"/></returns>
        /// <exception cref="Exception">Kraken api exception</exception>
        private IEnumerable<BaseData> GetOhlcBars(HistoryRequest request, string marketSymbol, TimeSpan period)
        {
            var resolution = ConvertResolution(request.Resolution);
            var url = $"/0/public/OHLC?pair={marketSymbol}&interval={resolution}";
            var start = (long) Time.DateTimeToUnixTimeStamp(request.StartTimeUtc);
            var end = (long) Time.DateTimeToUnixTimeStamp(request.EndTimeUtc);
            var resolutionInMs = (long) request.Resolution.ToTimeSpan().TotalSeconds;

            while (end - start >= resolutionInMs)
            {
                var timeframe = $"&since={start}";

                var response = MakeRequest(url + timeframe);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception($"KrakenBrokerage.GetOhlcHistory: request failed: [{(int) response.StatusCode}] {response.StatusDescription}, Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
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
                        Log.Debug($"KrakenBrokerage.GetOhlcHistory(): Received [{marketSymbol}] data for time period from {windowStartTime.ToStringInvariant()} to {windowEndTime.ToStringInvariant()}..");
                    }

                    start = (long) lastValue.Time + resolutionInMs;

                    foreach (var kline in candlesList)
                    {
                        if (kline.Time > end) // no "to" param in Kraken and it returns just 1000 candles since start timestamp
                        {
                            yield break;
                        }
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
        /// Method to retrieve tick resolution TradeBars
        /// </summary>
        /// <param name="request"><see cref="HistoryRequest"/></param>
        /// <param name="marketSymbol">Symbol name like in brokerage</param>
        /// <returns>List of <see cref="TradeBar"/></returns>
        /// <exception cref="Exception">Kraken api exception</exception>
        private IEnumerable<BaseData> GetTradeBars(HistoryRequest request, string marketSymbol)
        {
            var url = $"/0/public/Trades?pair={marketSymbol}";
            var start = Time.DateTimeToUnixTimeStamp(request.StartTimeUtc);
            var end = Time.DateTimeToUnixTimeStamp(request.EndTimeUtc);
            var period = request.Resolution.ToTimeSpan();

            while (end - start > 1) // allow 1 sec difference because of rounding from decimal to long
            {
                var timeframe = $"&since={start}";

                var response = MakeRequest(url + timeframe);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception($"KrakenBrokerage.GetTradeHistory: request failed: [{(int) response.StatusCode}] {response.StatusDescription}, Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
                }

                var token = JToken.Parse(response.Content);
                var tradesList = token["result"][marketSymbol].ToObject<List<KrakenTrade>>();

                if (tradesList.Count > 0)
                {
                    var lastValue = tradesList[^1];
                    if (Log.DebuggingEnabled)
                    {
                        var windowStartTime = Time.UnixTimeStampToDateTime(tradesList[0].Time);
                        var windowEndTime = Time.UnixTimeStampToDateTime(lastValue.Time);
                        Log.Debug($"KrakenBrokerage.GetTradeHistory(): Received [{marketSymbol}] data for time period from {windowStartTime.ToStringInvariant()} to {windowEndTime.ToStringInvariant()}..");
                    }

                    if (Math.Abs(start - lastValue.Time) == 0) // avoid duplicates
                    {
                        break;
                    }
                    
                    start = lastValue.Time;

                    foreach (var kline in tradesList)
                    {
                        if (kline.Time > end) // no "to" param in Kraken and it returns just 1000 candles since start timestamp
                        {
                            yield break;
                        }
                        
                        yield return new TradeBar()
                        {
                            Time = Time.UnixTimeStampToDateTime(kline.Time),
                            Symbol = request.Symbol,
                            Low = kline.Price,
                            High = kline.Price,
                            Open = kline.Price,
                            Close = kline.Price,
                            Volume = kline.Volume,
                            Value = kline.Price,
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
        /// Wrapper to all request logic 
        /// </summary>
        /// <param name="query">Api path</param>
        /// <param name="requestBody">Body of the request</param>
        /// <param name="method">Api method</param>
        /// <param name="isPrivate">Does need authentication</param>
        /// <returns><see cref="IRestResponse"/> for request</returns>
        private IRestResponse MakeRequest(string query, IDictionary<string, object> requestBody = null, Method method = Method.GET, bool isPrivate = false)
        {
            Dictionary<string, string> headers = null;
            if (isPrivate)
            {
                var nonce = Time.DateTimeToUnixTimeStampMilliseconds(DateTime.UtcNow).ConvertInvariant<long>();
                
                requestBody ??= new Dictionary<string, object>();
                
                requestBody.Add("nonce", nonce.ToString());
                
                headers = CreateSignature(query, nonce, BuildUrlEncode(requestBody));
            }

            var request = CreateRequest(query, headers, requestBody, method);

            return ExecuteRestRequest(request);
        }


        /// <summary>
        /// If an IP address exceeds a certain number of requests per minute
        /// HTTP 429 return code is used when breaking a request rate limit.
        /// </summary>
        /// <param name="request"><see cref="IRestRequest"/> request</param>
        /// <returns><see cref="IRestResponse"/> response</returns>
        private IRestResponse ExecuteRestRequest(IRestRequest request)
        {
            const int maxAttempts = 10;
            var attempts = 0;
            IRestResponse response;

            do
            {
                _rateLimiter.RateLimitCheck();
                
                response = RestClient.Execute(request);
                // 429 status code: Too Many Requests
            } while (++attempts < maxAttempts && (int)response.StatusCode == 429);

            return response;
        }

    }
}
