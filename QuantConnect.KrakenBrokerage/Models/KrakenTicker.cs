using System.Collections.Generic;
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
    public class KrakenTicker
    {
        /// <summary>
        /// Ask [price, whole lot volume, lot volume]
        /// </summary>
        public List<decimal> A { get; set; }
        
        /// <summary>
        /// Bid [price, whole lot volume, lot volume]
        /// </summary>
        public List<decimal> B { get; set; }
        
        /// <summary>
        /// Last trade closed [price, lot volume]
        /// </summary>
        public List<decimal> C { get; set; }
        
        /// <summary>
        /// Volume [today, last 24 hours]
        /// </summary>
        public List<decimal> V { get; set; }
        
        /// <summary>
        /// Volume weighted average price [today, last 24 hours]
        /// </summary>
        public List<decimal> P { get; set; }
        
        /// <summary>
        /// Number of trades [today, last 24 hours]
        /// </summary>
        public List<decimal> T { get; set; }
        
        /// <summary>
        /// Low [today, last 24 hours]
        /// </summary>
        public List<decimal> L { get; set; }
        
        /// <summary>
        /// High [today, last 24 hours]
        /// </summary>
        public List<decimal> H { get; set; }
        
        /// <summary>
        /// Today's opening price
        /// </summary>
        public decimal O { get; set; }
    }
}