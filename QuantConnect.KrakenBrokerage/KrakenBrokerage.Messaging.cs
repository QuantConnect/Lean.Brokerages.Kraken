using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.Kraken.Models;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.TimeInForces;
using QuantConnect.Securities;
using QuantConnect.Util;
using RestSharp;

namespace QuantConnect.Brokerages.Kraken
{
    public partial class KrakenBrokerage
    {
        /// <summary>
        /// Private message parser
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public override void OnMessage(object sender, WebSocketMessage e)
        {
            var data = (WebSocketClientWrapper.TextMessage) e.Data;

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
                    var response = token.ToObject<KrakenBaseWsResponse>();
                    if (response.Event == "heartbeat")
                    {
                        return;
                    }

                    if (response.Event == "systemStatus")
                    {
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Information, 200, $"KrakenWS system status: {token["status"]}"));
                        return;
                    }

                    if (response.Event == "addOrderStatus" && response.Status != "error")
                    {
                        var addOrder = token.ToObject<KrakenWsAddOrderResponse>();
                        var userref = addOrder.Reqid;
                        var brokerId = addOrder.Txid;
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

                    if (response.Event == "cancelOrderStatus" && response.Status != "error")
                    {
                        return;
                    }

                    if (response.Status == "error")
                    {
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, $"Error {token["event"]} event. Message: {token["errorMessage"]}"));
                        return;
                    }

                    if (response.Status == "subscribed")
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
                        var orderData = data.Value.ToObject<KrakenBaseWsResponse>();
                        if (data.Value.ToString().Contains("status"))
                        {
                            status = GetOrderStatus(orderData.Status);
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
                        var orderData = data.Value.ToObject<KrakenWsOpenOrder>();
                        var fillPrice = orderData.Price;
                        var fillQuantity = orderData.Vol_exec;
                        var orderFee = new OrderFee(new CashAmount(orderData.Fee, feeCurrency));
                        OrderDirection direction;

                        if (!data.Value.ToString().Contains("descr"))
                        {
                            if (!data.Value.ToString().Contains("status") && fillQuantity >= order.AbsoluteQuantity)
                            {
                                status = OrderStatus.Filled;
                            }
                            else
                            {
                                status = GetOrderStatus(orderData.Status);
                                updTime = !string.IsNullOrEmpty(orderData.LastUpdated) ? Time.UnixTimeStampToDateTime(orderData.LastUpdated.ConvertInvariant<double>()) : DateTime.UtcNow;
                            }

                            direction = order.Direction;
                            fillPrice = orderData.Avg_Price;
                        }
                        else
                        {
                            status = GetOrderStatus(orderData.Status);

                            direction = orderData.Descr.Type == "sell" ? OrderDirection.Sell : OrderDirection.Buy;
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
                    if (status is OrderStatus.Filled or OrderStatus.Canceled)
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
        
        private JsonObject CreateKrakenOrder(Order order, string token, out string symbol)
        {
            symbol = _symbolMapper.GetWebsocketSymbol(order.Symbol);

            var q = order.AbsoluteQuantity;
            var parameters = new JsonObject
            {
                {"event", "addOrder"},
                {"pair", symbol},
                {"volume", q.ToStringInvariant()},
                {"type", order.Direction == OrderDirection.Buy ? "buy" : "sell"},
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

            return parameters;
        }
    }
}