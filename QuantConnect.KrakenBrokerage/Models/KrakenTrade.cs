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

using Newtonsoft.Json;
using QuantConnect.Brokerages.Kraken.Converters;

namespace QuantConnect.Brokerages.Kraken.Models
{
    /// <summary>
    /// Kraken ws trade https://docs.kraken.com/websockets/#message-trade
    /// </summary>
    [JsonConverter(typeof(KrakenTradeConverter))]
    public class KrakenTrade
    {
        /// <summary>
        /// Price of Trade
        /// </summary>
        public decimal Price { get; set; }
        
        /// <summary>
        /// Volume of Trade
        /// </summary>
        public decimal Volume { get; set; }
        
        /// <summary>
        /// Time of trade, seconds since epoch
        /// </summary>
        public decimal Time { get; set; }
        
        /// <summary>
        /// Triggering order side, buy/sell
        /// </summary>
        public string Side { get; set; }
        
        /// <summary>
        /// Triggering order type market/limit
        /// </summary>
        public string OrderType { get; set; }
        
        /// <summary>
        /// Miscellaneous info
        /// </summary>
        public string Misc { get; set; }
    }
}