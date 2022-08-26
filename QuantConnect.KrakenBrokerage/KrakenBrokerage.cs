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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Api;
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
        private IAlgorithm _algorithm;
        private ISecurityProvider _securityProvider;
        private IDataAggregator _aggregator;
        private readonly KrakenSymbolMapper _symbolMapper = new KrakenSymbolMapper();
        private LiveNodePacket _job;
        private KrakenBrokerageRateLimits _rateLimiter;

        private const int MaximumSymbolsPerConnection = 50;
        private const string _apiUrl = "https://api.kraken.com";
        private const string _wsUrl = "wss://ws.kraken.com";
        private const string _wsAuthUrl = "wss://ws-auth.kraken.com";

        private readonly RateGate _webSocketRateLimiter = new RateGate(25, TimeSpan.FromSeconds(2));

        private readonly ConcurrentDictionary<int, decimal> _fills = new ConcurrentDictionary<int, decimal>();

        /// <summary>
        /// Constructor for brokerage
        /// </summary>
        public KrakenBrokerage() : base("Kraken")
        {
        }

        /// <summary>
        /// Constructor for brokerage
        /// </summary>
        /// <param name="apiKey">Api key</param>
        /// <param name="apiSecret">Api secret</param>
        /// <param name="verificationTier">Account verification tier</param>
        /// <param name="orderBookDepth">Desired depth of orderbook that will receive DataQueueHandler</param>
        /// <param name="algorithm"><see cref="IAlgorithm"/> instance</param>
        /// <param name="aggregator"><see cref="IDataAggregator"/> instance</param>
        /// <param name="job">Lean <see cref="LiveNodePacket"/></param>
        public KrakenBrokerage(string apiKey, string apiSecret, string verificationTier, int orderBookDepth, IAlgorithm algorithm, IDataAggregator aggregator, LiveNodePacket job)
            : base("Kraken")
        {
            Initialize(apiKey, apiSecret, verificationTier, orderBookDepth, algorithm, aggregator, job);
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
                Config.GetInt("kraken-orderbook-depth", 10),
                algorithm, aggregator, job)
        {
        }

        public override void Dispose()
        {
            _rateLimiter.DisposeSafely();
            _webSocketRateLimiter.DisposeSafely();
            SubscriptionManager.DisposeSafely();
        }

        public override bool IsConnected => WebSocket.IsOpen;

        /// <summary>
        /// Gets all Kraken orders not yet closed
        /// </summary>
        /// <returns>list of <see cref="Order"/></returns>
        public override List<Order> GetOpenOrders()
        {
            string query = "/0/private/OpenOrders";
            
            var token = MakeRequest(query, "GetOpenOrders", method:Method.POST, isPrivate: true);
            
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

            var token = MakeRequest(query, "GetAccountHoldings", param, Method.POST, true);

            var holdings = new List<Holding>();
            var result = token["result"];
            if (result == null)
            {
                return holdings;
            }

            foreach (JProperty balance in result.Children())
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
        public override List<CashAmount> GetCashBalance()
        {
            string query = "/0/private/Balance";
            
            var token = MakeRequest(query, "GetCashBalance", method:Method.POST, isPrivate: true);

            var cash = new List<CashAmount>();
            var result = token["result"];
            if (result == null)
            {
                return cash;
            }

            var dictBalances = result.ToObject<Dictionary<string, decimal>>();

            foreach (var balance in dictBalances)
            {
                cash.Add(new CashAmount(balance.Value, _symbolMapper.ConvertCurrency(balance.Key)));
            }

            var balances = cash.ToDictionary(x => x.Currency);
            
            if (_algorithm.BrokerageModel.AccountType == AccountType.Margin)
            {
                var holdings = GetAccountHoldings();
                for (var i = 0; i < holdings.Count; i++)
                {
                    CurrencyPairUtil.DecomposeCurrencyPair(holdings[i].Symbol, out var @base, out var quote);
                    
                    // Kraken margin balances logic
                    // Before position opened I had 80.397 USD, 0.047 ETH balances
                    // I opened position for 5 Usd, it's ~0.004 Eth. And balances remain 80.397 USD, 0.047 ETH.
                    // Then I closed positions(it was with negative pnl) and balances became 80.1 USD, 0.047 ETH.
                    
                    // Lean balances logic
                    // Before the position opened I had 100 USD, 0.1 ETH
                    // I opened the position for 0.01 Eth. And balances became 70 USD, 0.11 ETH.
                    // Then I closed positions(without pnl and fees) and balances became 100 USD, 0.1 ETH again.
                    
                    var baseQuantity = holdings[i].Quantity; // add Base holding to balance
                    
                    balances[@base] = balances.TryGetValue(@base, out var baseCurrencyAmount)
                        ? new CashAmount(baseQuantity + baseCurrencyAmount.Amount, @base)
                        : new CashAmount(baseQuantity, @base);

                    var quoteQuantity = -holdings[i].Quantity * holdings[i].AveragePrice; // substract quote holding value from balance
                    
                    balances[quote] = balances.TryGetValue(quote, out var quoteCurrencyAmount)
                        ? new CashAmount(quoteQuantity + quoteCurrencyAmount.Amount, quote)
                        : new CashAmount(quoteQuantity, quote);
                }
            }

            return balances.Values.ToList();
        }

        /// <summary>
        /// Place Kraken Order
        /// </summary>
        /// <param name="order"><see cref="Order"/> to place</param>
        /// <returns>Order placed or not</returns>
        public override bool PlaceOrder(Order order)
        {
            SetWebsocketToken();

            var parameters = CreateKrakenOrder(order);

            var json = JsonConvert.SerializeObject(parameters);

            _rateLimiter.OrderRateLimitCheck(order.Symbol);
            
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
            
            SetWebsocketToken();
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
        /// Initializes the instance of the class
        /// </summary>
        /// <param name="apiKey">Api key</param>
        /// <param name="apiSecret">Api secret</param>
        /// <param name="verificationTier">Account verification tier</param>
        /// <param name="orderBookDepth">Desired depth of orderbook that will receive DataQueueHandler</param>
        /// <param name="algorithm"><see cref="IAlgorithm"/> instance</param>
        /// <param name="aggregator"><see cref="IDataAggregator"/> instance</param>
        /// <param name="job">Lean <see cref="LiveNodePacket"/></param>
        protected void Initialize(string apiKey, string apiSecret, string verificationTier, int orderBookDepth, IAlgorithm algorithm, IDataAggregator aggregator, LiveNodePacket job)
        {
            if (IsInitialized)
            {
                return;
            }
            base.Initialize(_wsAuthUrl, new KrakenWebSocketWrapper(null), new RestClient(_apiUrl), apiKey, apiSecret);
            _algorithm = algorithm;
            _job = job;
            _aggregator = aggregator;
            _securityProvider = algorithm?.Portfolio;
            _orderBookDepth = orderBookDepth;
            _orderBookChannel = $"book-{_orderBookDepth}";

            _rateLimiter = new KrakenBrokerageRateLimits(verificationTier);
            _rateLimiter.Message += (_, e) => OnMessage(e);

            SubscriptionManager = new BrokerageMultiWebSocketSubscriptionManager(
                _wsUrl,
                MaximumSymbolsPerConnection,
                0,
                null,
                () => new KrakenWebSocketWrapper(null),
                Subscribe,
                Unsubscribe,
                OnDataMessage,
                TimeSpan.Zero,
                _webSocketRateLimiter);

            WebSocket.Open += (sender, args) => { SubscribeAuth(); };

            ValidateSubscription();
        }

        /// <summary>
        /// Method to retrieve minute+ resolution TradeBars
        /// </summary>
        /// <param name="request"><see cref="HistoryRequest"/></param>
        /// <param name="marketSymbol">Symbol name like in brokerage</param>
        /// <param name="period">period for <see cref="TradeBar"/></param>
        /// <returns>List of <see cref="TradeBar"/></returns>
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

                var token = MakeRequest(url + timeframe, "GetOhlcBars");
                
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

                    for (var i = 0; i < candlesList.Count; i++)
                    {
                        if (candlesList[i].Time > end) // no "to" param in Kraken and it returns just 1000 candles since start timestamp
                        {
                            yield break;
                        }
                        yield return new TradeBar()
                        {
                            Time = Time.UnixTimeStampToDateTime(candlesList[i].Time),
                            Symbol = request.Symbol,
                            Low = candlesList[i].Low,
                            High = candlesList[i].High,
                            Open = candlesList[i].Open,
                            Close = candlesList[i].Close,
                            Volume = candlesList[i].Volume,
                            Value = candlesList[i].Close,
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
        private IEnumerable<BaseData> GetTradeBars(HistoryRequest request, string marketSymbol)
        {
            var url = $"/0/public/Trades?pair={marketSymbol}";
            var start = Convert.ToDecimal(Time.DateTimeToUnixTimeStamp(request.StartTimeUtc));
            var end = Convert.ToDecimal(Time.DateTimeToUnixTimeStamp(request.EndTimeUtc));
            var period = request.Resolution.ToTimeSpan();

            while (end - start > 1) // allow 1 sec difference because of rounding from decimal to long
            {
                var timeframe = $"&since={start}";

                var token =  MakeRequest(url + timeframe, "GetTradeBars");
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

                    for (var i = 0; i < tradesList.Count; i++)
                    {
                        if (tradesList[i].Time > end) // no "to" param in Kraken and it returns just 1000 candles since start timestamp
                        {
                            yield break;
                        }
                        
                        yield return new TradeBar()
                        {
                            Time = Time.UnixTimeStampToDateTime(tradesList[i].Time),
                            Symbol = request.Symbol,
                            Low = tradesList[i].Price,
                            High = tradesList[i].Price,
                            Open = tradesList[i].Price,
                            Close = tradesList[i].Price,
                            Volume = tradesList[i].Volume,
                            Value = tradesList[i].Price,
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
        /// <param name="methodCaller">Method that calls request</param>
        /// <param name="requestBody">Body of the request</param>
        /// <param name="method">Api method</param>
        /// <param name="isPrivate">Does need authentication</param>
        /// <returns><see cref="IRestResponse"/> for request</returns>
        /// <exception cref="Exception">Kraken api exception</exception>
        private JToken MakeRequest(string query, string methodCaller, IDictionary<string, object> requestBody = null, Method method = Method.GET, bool isPrivate = false)
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
            
            var response = ExecuteRestRequest(request);

            var token = JToken.Parse(response.Content);
            
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception($"KrakenBrokerage.{methodCaller}: request failed: [{(int) response.StatusCode}] {response.StatusDescription}, Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
            }
            
            if (response.StatusCode == HttpStatusCode.OK && token["error"].HasValues)
            {
                throw new Exception($"KrakenBrokerage.{methodCaller}: request failed: {token["error"]}");
            }

            return token;
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

        private class ModulesReadLicenseRead : Api.RestResponse
        {
            [JsonProperty(PropertyName = "license")]
            public string License;
            [JsonProperty(PropertyName = "organizationId")]
            public string OrganizationId;
        }

        /// <summary>
        /// Validate the user of this project has permission to be using it via our web API.
        /// </summary>
        private static void ValidateSubscription()
        {
            try
            {
                var productId = 130;
                var userId = Config.GetInt("job-user-id");
                var token = Config.Get("api-access-token");
                var organizationId = Config.Get("job-organization-id", null);
                // Verify we can authenticate with this user and token
                var api = new ApiConnection(userId, token);
                if (!api.Connected)
                {
                    throw new ArgumentException("Invalid api user id or token, cannot authenticate subscription.");
                }
                // Compile the information we want to send when validating
                var information = new Dictionary<string, object>()
                {
                    {"productId", productId},
                    {"machineName", Environment.MachineName},
                    {"userName", Environment.UserName},
                    {"domainName", Environment.UserDomainName},
                    {"os", Environment.OSVersion}
                };
                // IP and Mac Address Information
                try
                {
                    var interfaceDictionary = new List<Dictionary<string, object>>();
                    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(nic => nic.OperationalStatus == OperationalStatus.Up))
                    {
                        var interfaceInformation = new Dictionary<string, object>();
                        // Get UnicastAddresses
                        var addresses = nic.GetIPProperties().UnicastAddresses
                            .Select(uniAddress => uniAddress.Address)
                            .Where(address => !IPAddress.IsLoopback(address)).Select(x => x.ToString());
                        // If this interface has non-loopback addresses, we will include it
                        if (!addresses.IsNullOrEmpty())
                        {
                            interfaceInformation.Add("unicastAddresses", addresses);
                            // Get MAC address
                            interfaceInformation.Add("MAC", nic.GetPhysicalAddress().ToString());
                            // Add Interface name
                            interfaceInformation.Add("name", nic.Name);
                            // Add these to our dictionary
                            interfaceDictionary.Add(interfaceInformation);
                        }
                    }
                    information.Add("networkInterfaces", interfaceDictionary);
                }
                catch (Exception)
                {
                    // NOP, not necessary to crash if fails to extract and add this information
                }
                // Include our OrganizationId is specified
                if (!string.IsNullOrEmpty(organizationId))
                {
                    information.Add("organizationId", organizationId);
                }
                var request = new RestRequest("modules/license/read", Method.POST) { RequestFormat = DataFormat.Json };
                request.AddParameter("application/json", JsonConvert.SerializeObject(information), ParameterType.RequestBody);
                api.TryRequest(request, out ModulesReadLicenseRead result);
                if (!result.Success)
                {
                    throw new InvalidOperationException($"Request for subscriptions from web failed, Response Errors : {string.Join(',', result.Errors)}");
                }

                var encryptedData = result.License;
                // Decrypt the data we received
                DateTime? expirationDate = null;
                long? stamp = null;
                bool? isValid = null;
                if (encryptedData != null)
                {
                    // Fetch the org id from the response if we are null, we need it to generate our validation key
                    if (string.IsNullOrEmpty(organizationId))
                    {
                        organizationId = result.OrganizationId;
                    }
                    // Create our combination key
                    var password = $"{token}-{organizationId}";
                    var key = SHA256.HashData(Encoding.UTF8.GetBytes(password));
                    // Split the data
                    var info = encryptedData.Split("::");
                    var buffer = Convert.FromBase64String(info[0]);
                    var iv = Convert.FromBase64String(info[1]);
                    // Decrypt our information
                    using var aes = new AesManaged();
                    var decryptor = aes.CreateDecryptor(key, iv);
                    using var memoryStream = new MemoryStream(buffer);
                    using var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read);
                    using var streamReader = new StreamReader(cryptoStream);
                    var decryptedData = streamReader.ReadToEnd();
                    if (!decryptedData.IsNullOrEmpty())
                    {
                        var jsonInfo = JsonConvert.DeserializeObject<JObject>(decryptedData);
                        expirationDate = jsonInfo["expiration"]?.Value<DateTime>();
                        isValid = jsonInfo["isValid"]?.Value<bool>();
                        stamp = jsonInfo["stamped"]?.Value<int>();
                    }
                }
                // Validate our conditions
                if (!expirationDate.HasValue || !isValid.HasValue || !stamp.HasValue)
                {
                    throw new InvalidOperationException("Failed to validate subscription.");
                }

                var nowUtc = DateTime.UtcNow;
                var timeSpan = nowUtc - Time.UnixTimeStampToDateTime(stamp.Value);
                if (timeSpan > TimeSpan.FromHours(12))
                {
                    throw new InvalidOperationException("Invalid API response.");
                }
                if (!isValid.Value)
                {
                    throw new ArgumentException($"Your subscription is not valid, please check your product subscriptions on our website.");
                }
                if (expirationDate < nowUtc)
                {
                    throw new ArgumentException($"Your subscription expired {expirationDate}, please renew in order to use this product.");
                }
            }
            catch (Exception e)
            {
                Log.Error($"ValidateSubscription(): Failed during validation, shutting down. Error : {e.Message}");
                Environment.Exit(1);
            }
        }
    }
}
