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

namespace QuantConnect.Brokerages.Kraken.Models
{
    public class KrakenOrderDescription
    {
        /// <summary>
        /// Asset pair
        /// </summary>
        public string Pair { get; set; }
        
        /// <summary>
        /// Type of order (buy/sell)
        /// </summary>
        public string Type { get; set; }
        
        /// <summary>
        /// Enum: "market" "limit" "stop-loss" "take-profit" "stop-loss-limit" "take-profit-limit" "settle-position"
        /// Order type
        /// </summary>
        public string OrderType { get; set; }
        
        /// <summary>
        /// primary price
        /// </summary>
        public decimal Price { get; set; }
        
        /// <summary>
        /// Secondary price
        /// </summary>
        public decimal Price2 { get; set; }
        
        /// <summary>
        /// Amount of leverage
        /// </summary>
        public string Leverage { get; set; }
        
        /// <summary>
        /// Order description
        /// </summary>
        public string Order { get; set; }
        
        /// <summary>
        /// Conditional close order description (if conditional close set)
        /// </summary>
        public string Close { get; set; }
    }
}