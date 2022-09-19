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
using System.Linq;
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
        
        private readonly ConcurrentDictionary<int, bool> _closedOrderEventSend = new ConcurrentDictionary<int, bool>();
        /// <summary>
        /// Private message parser
        /// </summary>
        /// <param name="sender">Sender</param>
        /// <param name="e"><see cref="WebSocketMessage"/> message</param>
        protected override void OnMessage(object sender, WebSocketMessage e)
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

                    if (response.Event == "addOrderStatus")
                    {
                        var addOrder = token.ToObject<KrakenWsAddOrderResponse>();
                        var userref = addOrder.Reqid;
                        var brokerId = addOrder.Txid;

                        CachedOrderIDs.TryGetValue(userref, out var order);
                        if (response.Status != "error")
                        {
                            if (order != null)
                            {
                                order.BrokerId.Clear();
                                order.BrokerId.Add(brokerId);

                                _closedOrderEventSend[order.Id] = false;
                            }
                            else
                            {
                                // this shouldn't happen but just in case
                                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, $"Unable to find order for request id: {userref}."));
                            }
                        }
                        else
                        {
                            var message = $"Error {token["event"]} event. Message: {token["errorMessage"]}";
                            if(order != null)
                            {
                                message += $". Order: {order}";
                            }
                            // error
                            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, message));
                        }
                        
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

                    var idOrder = CachedOrderIDs
                        .FirstOrDefault(o => o.Value.BrokerId.Contains(brokerId));

                    var order = idOrder.Value;

                    var orderId = idOrder.Key;

                    if (order == null)
                    {
                        order = _algorithm.Transactions.GetOrderByBrokerageId(brokerId);
                        if (order == null)
                        {
                            Log.Error($"EmitOrderEvent(): order not found: BrokerId: {brokerId}");
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

                    if (data.Value["vol_exec"] == null) // status update
                    {
                        var orderData = data.Value.ToObject<KrakenBaseWsResponse>();
                        if (data.Value["status"] != null)
                        {
                            status = GetOrderStatus(orderData.Status);
                        }
                        else if (data.Value["flags"].ToString().Contains("touched")) // Limit if touched order have been touched
                        {
                            status = OrderStatus.UpdateSubmitted;
                        }

                        orderEvent = new OrderEvent
                        (
                            orderId, order.Symbol, updTime, status,
                            order.Direction, 0, 0,
                            new OrderFee(new CashAmount(0m, feeCurrency)), $"Kraken Order Event {order.Direction}"
                        );
                    }
                    else
                    {
                        decimal fillPrice;
                        decimal fillQuantity;
                        OrderFee orderFee;
                        OrderDirection direction;

                        // snapshot of all open orders
                        // when connected all orders come with a description
                        if (data.Value["descr"] != null) 
                        {
                            var orderData = data.Value.ToObject<KrakenWsOpenOrder>();
                            
                            fillPrice = orderData.Price;
                            fillQuantity = orderData.Vol_exec;
                            orderFee = new OrderFee(new CashAmount(orderData.Fee, feeCurrency));
                            
                            status = GetOrderStatus(orderData.Status);

                            direction = orderData.Descr.Type == "sell" ? OrderDirection.Sell : OrderDirection.Buy;
                        }
                        // status update
                        // when order placed, canceled, rejected, expired or filled we receive status update
                        else if (data.Value["status"] != null)
                        {
                            var orderData = data.Value.ToObject<KrakenOrderStatusEvent>();
                            fillQuantity = orderData.Vol_exec;
                            orderFee = new OrderFee(new CashAmount(orderData.Fee, feeCurrency));
                            
                            status = GetOrderStatus(orderData.Status);
                            updTime = !string.IsNullOrEmpty(orderData.LastUpdated) ? Time.UnixTimeStampToDateTime(Convert.ToDecimal(orderData.LastUpdated)) : DateTime.UtcNow;

                            direction = order.Direction;
                            fillPrice = orderData.Avg_Price;
                        }
                        // trade execution
                        // when order filled (or partially filled) we receive information about this trade
                        // need this case because status isn't coming when order partially filled
                        else 
                        {
                            var orderData = data.Value.ToObject<KrakenTradeExecutionEvent>();
                            
                            fillQuantity = orderData.Vol_exec;
                            orderFee = new OrderFee(new CashAmount(orderData.Fee, feeCurrency));
                            
                            direction = order.Direction;
                            fillPrice = orderData.Avg_Price;
                        }

                        if (!_fills.TryGetValue(orderId, out var totalFilledQuantity))
                        {
                            _fills[orderId] = fillQuantity;
                            totalFilledQuantity = fillQuantity;
                        }
                        else
                        {
                            totalFilledQuantity += fillQuantity;
                            _fills[orderId] = totalFilledQuantity;
                        }

                        if (totalFilledQuantity == order.AbsoluteQuantity)
                        {
                            status = OrderStatus.Filled;
                        }

                        if (fillQuantity != 0 && status != OrderStatus.Filled)
                        {
                            status = OrderStatus.PartiallyFilled;
                        }
                        
                        if (direction == OrderDirection.Sell)
                        {
                            fillQuantity *= -1;
                        }

                        // when canceled it might not be present, which means we haven't seen it so it's okay, we didn't send the event yet
                        _closedOrderEventSend.TryGetValue(orderId, out var orderEventSent);
                            
                        if (status is OrderStatus.Filled && orderEventSent || status is OrderStatus.Canceled)
                        {
                            Order outOrder;
                            CachedOrderIDs.TryRemove(orderId, out outOrder);
                            _rateLimiter.OrderRateLimitDecay(order.Symbol);
                            _fills.TryRemove(orderId, out _);
                            _closedOrderEventSend.TryRemove(orderId, out _);
                        }

                        if (orderEventSent)
                        {
                            return;
                        }
                        
                        orderEvent = new OrderEvent
                        (
                            orderId, order.Symbol, updTime, status,
                            direction, fillPrice, fillQuantity,
                            orderFee, $"Kraken Order Event {direction}"
                        );
                    }
                    
                    OnOrderEvent(orderEvent);
                    
                    if (status == OrderStatus.Filled)
                    {
                        _closedOrderEventSend[orderId] = true;
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e);
                    throw;
                }
            }
        }
        
        private JsonObject CreateKrakenOrder(Order order)
        {
            var symbol = _symbolMapper.GetWebsocketSymbol(order.Symbol);

            var parameters = new JsonObject
            {
                {"event", "addOrder"},
                {"pair", symbol},
                {"volume", order.AbsoluteQuantity.ToStringInvariant()},
                {"type", order.Direction == OrderDirection.Buy ? "buy" : "sell"},
                {"token", WebsocketToken},
            };

            CachedOrderIDs[order.Id] = order;
            parameters.Add("reqid", order.Id);

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
                var propertiesList = new List<string>();
                if (krakenOrderProperties.PostOnly)
                {
                    propertiesList.Add("post");
                }

                if (krakenOrderProperties.FeeInBase)
                {
                    propertiesList.Add("fcib");
                }

                if (krakenOrderProperties.FeeInQuote)
                {
                    propertiesList.Add("fciq");
                }

                if (krakenOrderProperties.NoMarketPriceProtection)
                {
                    propertiesList.Add("nompp");
                }

                if (propertiesList.Count != 0)
                {
                    parameters.Add("oflags", string.Join(",", propertiesList));
                }

                if (krakenOrderProperties.ConditionalOrder != null)
                {
                    if (krakenOrderProperties.ConditionalOrder is MarketOrder)
                    {
                        throw new BrokerageException($"KrakenBrokerage.PlaceOrder: Conditional order type can't be Market. Specify other order type");
                    }
                    if (krakenOrderProperties.ConditionalOrder is LimitOrder limitOrd)
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