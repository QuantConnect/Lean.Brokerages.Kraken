using Newtonsoft.Json;
using QuantConnect.Brokerages.Kraken.Converters;

namespace QuantConnect.Brokerages.Kraken.Models
{
    /// <summary>
    /// Kraken ws spread https://docs.kraken.com/websockets/#message-spread
    /// </summary>
    [JsonConverter(typeof(KrakenSpreadConverter))]
    public class KrakenSpread
    {
        /// <summary>
        /// Bid price
        /// </summary>
        public decimal Bid { get; set; }
        
        /// <summary>
        /// Ask price
        /// </summary>
        public decimal Ask { get; set; }
        
        /// <summary>
        /// Time, seconds since epoch
        /// </summary>
        public double Timestamp { get; set; }
        
        /// <summary>
        /// Bid Volume
        /// </summary>
        public decimal BidVolume { get; set; }
        
        /// <summary>
        /// Ask Volume
        /// </summary>
        public decimal AskVolume { get; set; }
    }
}