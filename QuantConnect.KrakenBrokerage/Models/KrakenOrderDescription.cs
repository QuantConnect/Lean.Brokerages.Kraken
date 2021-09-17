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