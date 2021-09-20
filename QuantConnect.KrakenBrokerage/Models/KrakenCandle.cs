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
        public double Time { get; set; }
        
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