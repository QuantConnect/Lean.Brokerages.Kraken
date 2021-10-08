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
    /// Kraken candle
    /// </summary>
    [JsonConverter(typeof(KrakenCandlesConverter))]
    public class KrakenCandle
    {
        /// <summary>
        /// Candle Begin timestamp
        /// </summary>
        public decimal Time { get; set; }
        
        /// <summary>
        /// Market ticker
        /// </summary>
        public string Symbol { get; set; }
        
        /// <summary>
        /// Open price
        /// </summary>
        public decimal Open { get; set; }
        
        /// <summary>
        /// High price
        /// </summary>
        public decimal High { get; set; }
        
        /// <summary>
        /// Low price
        /// </summary>
        public decimal Low { get; set; }
        
        /// <summary>
        /// Close price
        /// </summary>
        public decimal Close { get; set; }
        
        /// <summary>
        /// Volume weighted average price
        /// </summary>
        public decimal VWap { get; set; }
        
        /// <summary>
        /// Traded volume
        /// </summary>
        public decimal Volume { get; set; }
        
        /// <summary>
        /// Number of trades included in candle 
        /// </summary>
        public decimal Count { get; set; }
    }
}