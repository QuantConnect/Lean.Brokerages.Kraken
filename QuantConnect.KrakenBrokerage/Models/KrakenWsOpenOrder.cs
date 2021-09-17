namespace QuantConnect.Brokerages.Kraken.Models
{
    public class KrakenWsOpenOrder : KrakenOpenOrder
    {
        /// <summary>
        /// Last update timestamp
        /// </summary>
        public string LastUpdated { get; set; }
        
        /// <summary>
        /// average price (cumulative; quote currency unless viqc set in oflags)
        /// </summary>
        public decimal Avg_Price { get; set; }
    }
}