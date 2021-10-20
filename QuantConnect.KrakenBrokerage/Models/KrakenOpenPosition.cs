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
    public class KrakenOpenPosition
    {
        /// <summary>
        /// Order ID responsible for the position
        /// </summary>
        public string OrderTxid { get; set; }
        
        /// <summary>
        /// Value: "open"
        /// Position status
        /// </summary>
        public string PosStatus { get; set; }
        
        /// <summary>
        /// Asset pair
        /// </summary>
        public string Pair { get; set; }
        
        /// <summary>
        /// 
        /// </summary>
        public decimal Time { get; set; }
        
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
        /// Position opening size (in base currency)
        /// </summary>
        public decimal Vol { get; set; }
        
        /// <summary>
        /// Quantity closed (in base currency)
        /// </summary>
        public decimal Vol_closed { get; set; }
        
        /// <summary>
        /// Opening cost of position (in quote currency)
        /// </summary>
        public decimal Cost { get; set; }
        
        /// <summary>
        /// Opening fee of position (in quote currency)
        /// </summary>
        public decimal Fee { get; set; }
        
        /// <summary>
        /// Initial margin consumed (in quote currency)
        /// </summary>
        public decimal Margin { get; set; }
        
        /// <summary>
        /// Current value of remaining position
        /// </summary>
        public decimal Value { get; set; }
        
        /// <summary>
        /// Unrealised P&L of remaining position
        /// </summary>
        public decimal Net { get; set; }
        
        /// <summary>
        /// Funding cost and term of position
        /// </summary>
        public string Terms { get; set; }
        
        /// <summary>
        /// Timestamp of next margin rollover fee
        /// </summary>
        public decimal Rollovertm { get; set; }
        
        /// <summary>
        /// Comma delimited list of add'l info
        /// </summary>
        public string Misc { get; set; }
        
        /// <summary>
        ///Comma delimited list of order flags
        /// post post-only order (available when ordertype = limit)
        /// fcib prefer fee in base currency (default if selling)
        /// fciq prefer fee in quote currency (default if buying, mutually exclusive with fcib)
        /// nompp disable market price protection for market orders
        /// </summary>
        public string Oflags { get; set; }
    }
}