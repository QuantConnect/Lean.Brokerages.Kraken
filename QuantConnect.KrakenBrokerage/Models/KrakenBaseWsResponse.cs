namespace QuantConnect.Brokerages.Kraken.Models
{
    /// <summary>
    /// Kraken base websocket response - every ws response could parse in it
    /// </summary>
    public class KrakenBaseWsResponse
    {
        /// <summary>
        /// Response status - success, error etc
        /// </summary>
        public string Status { get; set; }
        
        /// <summary>
        /// Event type - addOrder, cancelOrder, openOrder etc
        /// </summary>
        public string Event { get; set; }
    }
}