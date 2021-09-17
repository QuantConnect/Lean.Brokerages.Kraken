using System.Collections.Generic;

namespace QuantConnect.Brokerages.Kraken.Models
{
    public class KrakenOpenOrder
    {
        /// <summary>
        /// Referral order transaction ID that created this order (Not a broker id / order id)
        /// </summary>
        public string Refid { get; set; }
        
        /// <summary>
        /// User reference id
        /// </summary>
        public string UserRef { get; set; }
        
        /// <summary>
        /// Status of order
        /// pending = order pending book entry
        /// open = open order
        /// closed = closed order
        /// canceled = order canceled
        /// expired = order expired
        /// </summary>
        public string Status { get; set; }
        
        /// <summary>
        /// Unix timestamp of when order was placed
        /// </summary>
        public string Opentm { get; set; }
        
        /// <summary>
        /// Unix timestamp of order start time (or 0 if not set)
        /// </summary>
        public string Starttm { get; set; }
        
        /// <summary>
        /// Unix timestamp of order end time (or 0 if not set)
        /// </summary>
        public string Expiretm { get; set; }
        
        /// <summary>
        /// Order description info
        /// </summary>
        public KrakenOrderDescription Descr { get; set; }
        
        /// <summary>
        /// Volume of order (base currency)
        /// </summary>
        public decimal Vol { get; set; }
        
        /// <summary>
        /// Volume executed (base currency)
        /// </summary>
        public decimal Vol_exec { get; set; }
        
        /// <summary>
        /// Total cost (quote currency unless)
        /// </summary>
        public decimal Cost { get; set; }
        
        /// <summary>
        /// Total fee (quote currency)
        /// </summary>
        public decimal Fee { get; set; }
        
        /// <summary>
        /// Average price (quote currency)
        /// </summary>
        public decimal Price { get; set; }
        
        /// <summary>
        /// Stop price (quote currency)
        /// </summary>
        public decimal StopPrice { get; set; }
        
        /// <summary>
        /// Triggered limit price (quote currency, when limit based order type triggered)
        /// </summary>
        public decimal LimitPrice { get; set; }
        
        /// <summary>
        /// Comma delimited list of miscellaneous info
        /// stopped triggered by stop price
        /// touched triggered by touch price
        /// liquidated liquidation
        /// partial partial fill
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