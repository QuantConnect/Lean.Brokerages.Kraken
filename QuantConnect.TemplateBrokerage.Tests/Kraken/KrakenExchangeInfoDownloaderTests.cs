using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Logging;
using QuantConnect.ToolBox.KrakenDownloader;

namespace QuantConnect.Tests.Brokerages.Kraken
{
    public class KrakenExchangeInfoDownloaderTests
    {
        [Test]
        public void GetsExchangeInfo()
        {
            var downloader = new KrakenExchangeInfoDownloader();
            var tickers = downloader.Get().ToList();

            Assert.IsTrue(tickers.Any());

            foreach (var t in tickers)
            {
                Assert.IsTrue(t.StartsWith(Market.Kraken, StringComparison.OrdinalIgnoreCase));
            }

            Log.Trace("Tickers retrieved: " + tickers.Count);
        }
    }
}
