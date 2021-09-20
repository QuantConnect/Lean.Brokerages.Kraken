using Newtonsoft.Json;
using QuantConnect.Brokerages.Kraken.Converters;

namespace QuantConnect.Brokerages.Kraken.Models
{
    /// <summary>
    /// Kraken ws trade https://docs.kraken.com/websockets/#message-trade
    /// </summary>
    [JsonConverter(typeof(KrakenTradeConverter))]
    public class KrakenTrade
    {
        /// <summary>
        /// Price of Trade
        /// </summary>
        public decimal Price { get; set; }
        
        /// <summary>
        /// Volume of Trade
        /// </summary>
        public decimal Volume { get; set; }
        
        /// <summary>
        /// Time of trade, seconds since epoch
        /// </summary>
        public double Time { get; set; }
        
        /// <summary>
        /// Triggering order side, buy/sell
        /// </summary>
        public string Side { get; set; }
        
        /// <summary>
        /// Triggering order type market/limit
        /// </summary>
        public string OrderType { get; set; }
        
        /// <summary>
        /// Miscellaneous info
        /// </summary>
        public string Misc { get; set; }
    }
}