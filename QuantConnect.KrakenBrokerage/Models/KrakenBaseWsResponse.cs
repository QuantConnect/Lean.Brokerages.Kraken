namespace QuantConnect.Brokerages.Kraken.Models
{
    public class KrakenBaseWsResponse
    {
        public string Status { get; set; }
        public string Event { get; set; }
    }
}