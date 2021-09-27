namespace QuantConnect.Brokerages.Kraken.Models
{
    public enum KrakenRateLimitType
    {
        Common,
        Orders,
        Cancel
    }

    public enum KrakenVerificationTier
    {
        Starter,
        Intermediate,
        Pro
    }
    
}
