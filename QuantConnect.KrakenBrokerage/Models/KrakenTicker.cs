using System.Collections.Generic;

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