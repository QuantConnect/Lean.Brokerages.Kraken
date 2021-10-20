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
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.Kraken.Models;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using RestSharp;

namespace QuantConnect.Brokerages.Kraken
{
    public partial class KrakenBrokerage
    {
        /// <summary>
        /// Get current tick of Symbol
        /// </summary>
        /// <param name="symbol">Kraken <see cref="Symbol"/></param>
        /// <returns><see cref="Tick"/></returns>
        /// <exception cref="Exception">Kraken api exception</exception>
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
        
        /// <summary>
        /// Create sign to enter private rest info
        /// </summary>
        /// <param name="path">Api path</param>
        /// <param name="nonce">Unix timestamp</param>
        /// <param name="body">UrlEncoded body</param>
        /// <returns></returns>
        private Dictionary<string, string> CreateSignature(string path, long nonce, string body = "")
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

        /// <summary>
        /// Request builder
        /// </summary>
        /// <param name="query">Api path</param>
        /// <param name="headers">Headers of request</param>
        /// <param name="requestBody">Body of request</param>
        /// <param name="method">Request method</param>
        /// <returns><see cref="IRestRequest"/></returns>
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
    }
}