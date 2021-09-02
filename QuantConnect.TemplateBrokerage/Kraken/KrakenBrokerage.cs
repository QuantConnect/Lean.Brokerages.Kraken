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
    public class KrakenBrokerage : BaseWebsocketsBrokerage, IDataQueueHandler
    {
        private readonly IAlgorithm _algorithm;
        private readonly IDataAggregator _aggregator;
        private readonly KrakenSymbolMapper _symbolMapper = new KrakenSymbolMapper();
        private LiveNodePacket _job;

        private const string _apiUrl = "https://api.kraken.com/0";
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
            
            ApiKey = apiKey;
            ApiSecret = apiSecret;

            RateLimitsOrderPerSymbolDictionary = new Dictionary<string, int>();
            RateLimitsCancelPerSymbolDictionary = new Dictionary<string, decimal>();

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
        

        public override bool IsConnected { get; } = true;
        public override List<Order> GetOpenOrders()
        {
            return new ();
        }

        public override List<Holding> GetAccountHoldings()
        {
            return new ();
        }

        public override List<CashAmount> GetCashBalance()
        {
            return new ();
        }

        public override bool PlaceOrder(Order order)
        {
            throw new NotImplementedException();
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

        public override bool CancelOrder(Order order)
        {
            throw new NotImplementedException();
        }
        
        /// <summary>
        /// Closes the websockets connection
        /// </summary>
        public override void Disconnect()
        {
            WebSocket.Close();
            _webSocketRateLimiter.DisposeSafely();
        }

        public override void OnMessage(object sender, WebSocketMessage e)
        {
            throw new NotImplementedException();
        }

        public override void Subscribe(IEnumerable<Symbol> symbols)
        {
            throw new NotImplementedException();
        }

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
        /// Sets the job we're subscribing for
        /// </summary>
        /// <param name="job">Job we're subscribing for</param>
        public void SetJob(LiveNodePacket job)
        {
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
            string url = _apiUrl + $"/public/OHLC?pair={marketSymbol}&interval={resolution}";
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
        
        private static string BuildUrlEncode(IDictionary<string, string> args) => string.Join(
            "&",
            args.Where(x => x.Value != null).Select(x => x.Key + "=" + x.Value)
        );
        
        private IRestRequest CreateRequest(string query, Dictionary<string, string> headers = null, IDictionary<string, string> requestBody = null, Method method = Method.GET)
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
        private IRestResponse ExecuteRestRequest(IRestRequest request, bool isOrderRequest = false)
        {
            const int maxAttempts = 10;
            var attempts = 0;
            IRestResponse response;

            do
            {
                if (!isOrderRequest)
                {
                    RateLimitCheck();
                }

                response = RestClient.Execute(request);
                // 429 status code: Too Many Requests
            } while (++attempts < maxAttempts && (int)response.StatusCode == 429);

            return response;
        }

    }
}
