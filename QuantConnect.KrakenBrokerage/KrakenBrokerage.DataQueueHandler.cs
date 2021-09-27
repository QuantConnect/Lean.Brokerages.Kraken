using System;
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
        /// Token needs to have access to auth wss. Initialized in SubscribeAuth
        /// </summary>
        private string WebsocketToken { get; set; }

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
                        Log.Trace($"KrakenBrokerage.On{token["status"]}(): Channel subscribed: Id:{token["channelID"]} {token["pair"]}/{token["channelName"]}");
                        break;
                }
            }
            else if (token is JArray)
            {
                switch (token[2].ToString())
                {
                    case "spread":
                        EmitQuoteTick(_symbolMapper.GetSymbolFromWebsocket(token[3].ToString()), token[1].ToObject<KrakenSpread>());
                        break;
                    case "trade":
                        ParseTradeMessage(_symbolMapper.GetSymbolFromWebsocket(token[3].ToString()), token[1].ToObject<List<KrakenTrade>>());
                        break;
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

        private void EmitQuoteTick(Symbol symbol, KrakenSpread spread)
        {
            var tick = new Tick
            {
                AskPrice = spread.Ask,
                BidPrice = spread.Bid,
                Time = Time.UnixTimeStampToDateTime(spread.Timestamp),
                Symbol = symbol,
                TickType = TickType.Quote,
                AskSize = Math.Abs(spread.AskVolume),
                BidSize = Math.Abs(spread.BidVolume),
                Exchange = Market.Kraken
            };
            
            tick.SetValue();
            EmitTick(tick);
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
               WebsocketToken = GetWebsocketToken();

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