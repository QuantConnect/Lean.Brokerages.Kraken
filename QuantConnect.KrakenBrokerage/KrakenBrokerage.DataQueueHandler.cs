using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.Kraken.Models;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Util;
using RestSharp;

namespace QuantConnect.Brokerages.Kraken
{
    public partial class KrakenBrokerage
    {
        /// <summary>
        /// Locking object for the Ticks list in the data queue handler
        /// </summary>
        public readonly object TickLocker = new object();

        /// <summary>
        /// Need Token to have access to auth wss
        /// </summary>
        private string WebsocketToken { get; set; }
        
        private readonly ConcurrentDictionary<Symbol, DefaultOrderBook> _orderBooks = new ConcurrentDictionary<Symbol, DefaultOrderBook>();

        private const int _orderBookDepth = 25; // Valid Options are: 10, 25, 100, 500, 1000

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
                    depth = _orderBookDepth, 
                    name = "book"
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
                    depth = _orderBookDepth,
                    name = "book"
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

        /// <summary>
        /// Parse public ws message
        /// </summary>
        /// <param name="webSocketMessage"> message</param>
        private void OnDataMessage(WebSocketMessage webSocketMessage)
        {
            var e = (WebSocketClientWrapper.TextMessage) webSocketMessage.Data;
            var token = JToken.Parse(e.Message);

            if (token is JObject)
            {
                switch (token["event"].ToString())
                {
                    case "pong":
                    case "heartbeat":
                        return;
                    case "subscriptionStatus":
                        Log.Trace($"KrakenBrokerage.On{token["status"]}(): Channel {token["status"]}: Id:{token["channelID"]} {token["pair"]}/{token["channelName"]}");
                        break;
                }
            }
            else if (token is JArray)
            {
                if (token[2].ToString() == $"book-{_orderBookDepth}")
                {
                    if (token.ToString().Contains("as") || token.ToString().Contains("bs")) // snapshot
                    {
                        ProcessOrderBookSnapshot(_symbolMapper.GetSymbolFromWebsocket(token[3].ToString()), token[1]);
                    }
                    else
                    {
                        ProcessOrderBookUpdate(_symbolMapper.GetSymbolFromWebsocket(token[3].ToString()), token[1]);
                    }
                    
                }
                else if (token[2].ToString() == "trade")
                {
                    ParseTradeMessage(_symbolMapper.GetSymbolFromWebsocket(token[3].ToString()), token[1].ToObject<List<KrakenTrade>>());
                }
            }
        }

        /// <summary>
        /// Parse public ws trade message
        /// </summary>
        /// <param name="symbol">Symbol</param>
        /// <param name="trades">list of trades</param>
        private void ParseTradeMessage(Symbol symbol, List<KrakenTrade> trades)
        {
            foreach (var data in trades)
            {
                EmitTradeTick(symbol, Time.UnixTimeStampToDateTime(data.Time), data.Price, data.Volume);
            }
        }

        private void EmitTradeTick(Symbol symbol, DateTime time, decimal price, decimal amount)
        {
            try
            {
                EmitTick(new Tick
                {
                    Value = price,
                    Time = time,
                    Symbol = symbol,
                    TickType = TickType.Trade,
                    Quantity = Math.Abs(amount),
                    Exchange =  Market.Kraken
                });
            }
            catch (Exception e)
            {
                Log.Error(e);
                throw;
            }
        }

        /// <summary>
        /// Parse ws orderbook snapshot message
        /// </summary>
        /// <param name="symbol">Symbol</param>
        /// <param name="book">JToken with asks and bids</param>
        private void ProcessOrderBookSnapshot(Symbol symbol, JToken book)
        {
            try
            {
                if (!_orderBooks.TryGetValue(symbol, out var orderBook))
                {
                    orderBook = new DefaultOrderBook(symbol);
                    _orderBooks[symbol] = orderBook;
                }
                else
                {
                    orderBook.BestBidAskUpdated -= OnBestBidAskUpdated;
                    orderBook.Clear();
                }

                foreach (var entry in book["as"])
                {
                    var bidAsk = entry.ToObject<KrakenBidAsk>();
                    orderBook.UpdateAskRow(bidAsk.Price, bidAsk.Volume);
                }
                
                foreach (var entry in book["bs"])
                {
                    var bidAsk = entry.ToObject<KrakenBidAsk>();
                    orderBook.UpdateBidRow(bidAsk.Price, bidAsk.Volume);
                }

                orderBook.BestBidAskUpdated += OnBestBidAskUpdated;

                EmitQuoteTick(symbol, orderBook.BestBidPrice, orderBook.BestBidSize, orderBook.BestAskPrice, orderBook.BestAskSize);
            }
            catch (Exception e)
            {
                Log.Error(e, $"Entries: [{book}]");
                throw;
            }
        }
        
        /// <summary>
        /// Parse ws book update websocket message
        /// </summary>
        /// <param name="symbol">Symbol</param>
        /// <param name="book">JToken with update</param>
        private void ProcessOrderBookUpdate(Symbol symbol, JToken book)
        {
            try
            {
                var orderBook = _orderBooks[symbol];

                if (book.ToString().Contains("a"))
                {
                    foreach (var ask in book["a"])
                    {
                        var bidAsk = ask.ToObject<KrakenBidAsk>();
                        orderBook.UpdateAskRow(bidAsk.Price, bidAsk.Volume);
                    }
                }
                
                if (book.ToString().Contains("b"))
                {
                    foreach (var bid in book["b"])
                    {
                        var bidAsk = bid.ToObject<KrakenBidAsk>();
                        orderBook.UpdateBidRow(bidAsk.Price, bidAsk.Volume);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e, $"Entries: [{book}]");
                throw;
            }
        }
        
        private void EmitQuoteTick(Symbol symbol, decimal bidPrice, decimal bidSize, decimal askPrice, decimal askSize)
        {
            try
            {
                var tick = new Tick
                {
                    AskPrice = askPrice,
                    BidPrice = bidPrice,
                    Time = DateTime.UtcNow,
                    Symbol = symbol,
                    TickType = TickType.Quote,
                    AskSize = Math.Abs(askSize),
                    BidSize = Math.Abs(bidSize),
                    Exchange = Market.Kraken
                };
            
                tick.SetValue();
                EmitTick(tick);
            }
            catch (Exception e)
            {
                Log.Error(e);
                throw;
            }
        }

        /// <summary>
        /// Emit stream tick
        /// </summary>
        /// <param name="tick"></param>
        public void EmitTick(Tick tick)
        {
            lock (TickLocker)
            {
                _aggregator.Update(tick);
            }
        }
        
        private void OnBestBidAskUpdated(object sender, BestBidAskUpdatedEventArgs e)
        {
            EmitQuoteTick(e.Symbol, e.BestBidPrice, e.BestBidSize, e.BestAskPrice, e.BestAskSize);
        }

        #endregion

        #region IDataQueueHandler

        /// <summary>
        /// Get websocket token. Needs when subscribing to private feeds 
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private string GetWebsocketToken()
        {
            var query = "/0/private/GetWebSocketsToken";

            var response = MakeRequest(query, method:Method.POST, isPrivate: true);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception($"KrakenBrokerage.GetCashBalance: request failed: [{(int) response.StatusCode}] {response.StatusDescription}, Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
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

        /// <summary>
        /// Subscribe to auth channel
        /// </summary>
        private void SubscribeAuth()
        {
            if (WebSocket.IsOpen)
            {
               WebsocketToken = string.IsNullOrEmpty(WebsocketToken) ? GetWebsocketToken() : WebsocketToken;

                WebSocket.Send(JsonConvert.SerializeObject(new
                {
                    @event = "subscribe",
                    subscription = new
                    {
                        token = WebsocketToken,
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
        /// Should be empty as handled in <see cref="BrokerageMultiWebSocketSubscriptionManager"/>
        /// </summary>
        /// <param name="symbols"></param>
        public override void Subscribe(IEnumerable<Symbol> symbols)
        {
        }

        #endregion
    }
}