namespace QuantConnect.Brokerages.Kraken.Models
{
    public class KrakenWsOpenOrder : KrakenOpenOrder
    {
        public string LastUpdated { get; set; }
        public decimal Avg_Price { get; set; }
    }
}