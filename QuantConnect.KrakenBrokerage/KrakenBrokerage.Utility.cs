using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.Kraken.Models;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using RestSharp;

namespace QuantConnect.Brokerages.Kraken
{
    public partial class KrakenBrokerage
    {
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

        private OrderStatus GetOrderStatus(string status) => status switch
        {
            "pending" => OrderStatus.New,
            "open" => OrderStatus.Submitted,
            "closed" => OrderStatus.Filled,
            "expired" => OrderStatus.Canceled,
            "canceled" => OrderStatus.Canceled,
            _ => OrderStatus.None
        };

        private static string BuildUrlEncode(IDictionary<string, object> args) => string.Join(
            "&",
            args.Where(x => x.Value != null).Select(x => x.Key + "=" + x.Value)
        );

        private IRestRequest CreateRequest(string query, Dictionary<string, string> headers = null, IDictionary<string, object> requestBody = null, Method method = Method.GET)
        {
            RestRequest request = new RestRequest(query) {Method = method};

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

        public Tick GetTick(Symbol symbol)
        {
            var marketSymbol = _symbolMapper.GetBrokerageSymbol(symbol);

            var restRequest = CreateRequest($"/0/public/Ticker?pair={marketSymbol}");

            var response = ExecuteRestRequest(restRequest);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception($"KrakenBrokerage.GetTick: request failed: [{(int) response.StatusCode}] {response.StatusDescription}, Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
            }

            var token = JToken.Parse(response.Content);

            var element = token["result"].First as JProperty;

            var ticker = element.Value.ToObject<KrakenTicker>();

            var tick = new Tick
            {
                AskPrice = ticker.A[0],
                BidPrice = ticker.B[0],
                Value = ticker.C[0],
                Time = DateTime.UtcNow,
                Symbol = symbol,
                TickType = TickType.Quote,
                AskSize = ticker.A[2],
                BidSize = ticker.B[2],
                Exchange = "kraken",
            };

            return tick;
        }
    }
}