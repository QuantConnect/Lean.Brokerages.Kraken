using System;
using Newtonsoft.Json;
using QuantConnect.Brokerages.Kraken.Converters;

namespace QuantConnect.Brokerages.Kraken.Models
{
    /// <summary>
    /// Kraken bid or ask model
    /// </summary>
    [JsonConverter(typeof(KrakenBidAskConverter))]
    public class KrakenBidAsk
    {
        /// <summary>
        /// Ask or bid price
        /// </summary>
        public decimal Price { get; set; }
        
        /// <summary>
        /// Ask or bid volume
        /// </summary>
        public decimal Volume { get; set; }
        
        /// <summary>
        /// Ask or bid time
        /// </summary>
        public double Timestamp { get; set; }
    }
}