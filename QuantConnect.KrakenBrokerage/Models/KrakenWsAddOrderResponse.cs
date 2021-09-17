namespace QuantConnect.Brokerages.Kraken.Models
{
    public class KrakenWsAddOrderResponse : KrakenBaseWsResponse
    {
        /// <summary>
        /// client originated requestID sent as acknowledgment in the message response
        /// </summary>
        public int Reqid { get; set; }
        
        /// <summary>
        /// order ID
        /// </summary>
        public string Txid { get; set; }
    }
}