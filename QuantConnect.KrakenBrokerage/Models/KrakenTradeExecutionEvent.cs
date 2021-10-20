namespace QuantConnect.Brokerages.Kraken.Models
{
    public class KrakenTradeExecutionEvent
    {
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
        /// average price (cumulative; quote currency unless viqc set in oflags)
        /// </summary>
        public decimal Avg_Price { get; set; }
        
        /// <summary>
        /// User reference id
        /// </summary>
        public string UserRef { get; set; }
    }
}