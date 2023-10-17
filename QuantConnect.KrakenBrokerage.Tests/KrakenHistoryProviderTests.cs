/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Linq;
using NodaTime;
using NUnit.Framework;
using QuantConnect.Brokerages.Kraken;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.HistoricalData;
using QuantConnect.Logging;
using QuantConnect.Securities;

namespace QuantConnect.Tests.Brokerages.Kraken
{
    public partial class KrakenBrokerageTests
    {
        [Test]
        [TestCaseSource(nameof(ValidHistory))]
        [TestCaseSource(nameof(InvalidHistory))]
        public void GetsHistory(Symbol symbol, Resolution resolution, TimeSpan period, bool throwsException)
        {
            TestDelegate test = () =>
            {
                var brokerage = (KrakenBrokerage)Brokerage;

                var historyProvider = new BrokerageHistoryProvider();
                historyProvider.SetBrokerage(brokerage);
                historyProvider.Initialize(new HistoryProviderInitializeParameters(null, null, null, null, null, null, null, false, new DataPermissionManager(), null));

                var now = DateTime.UtcNow;

                var requests = new[]
                {
                    new HistoryRequest(now.Add(-period),
                                       now,
                                       typeof(TradeBar),
                                       symbol,
                                       resolution,
                                       SecurityExchangeHours.AlwaysOpen(TimeZones.Utc),
                                       DateTimeZone.Utc,
                                       Resolution.Minute,
                                       false,
                                       false,
                                       DataNormalizationMode.Adjusted,
                                       TickType.Trade)
                };

                var history = historyProvider.GetHistory(requests, TimeZones.Utc);
                
                foreach (var slice in history)
                {
                    var bar = slice.Bars[symbol];

                    Log.Debug($"{bar.Time}: {bar}");
                }

                Log.Trace("Data points retrieved: " + historyProvider.DataPointCount);
            };

            if (throwsException)
            {
                Assert.Throws<ArgumentException>(test);
            }
            else
            {
                Assert.DoesNotThrow(test);
            }
        }

        [Test]
        [TestCaseSource(nameof(NoHistory))]
        public void GetEmptyHistory(Symbol symbol, Resolution resolution, TimeSpan period)
        {
            TestDelegate test = () =>
            {
                var brokerage = (KrakenBrokerage)Brokerage;

                var historyProvider = new BrokerageHistoryProvider();
                historyProvider.SetBrokerage(brokerage);
                historyProvider.Initialize(new HistoryProviderInitializeParameters(null, null, null, null, null, null, null,false, new DataPermissionManager(), null));

                var now = DateTime.UtcNow;

                var requests = new[]
                {
                    new HistoryRequest(now.Add(-period),
                                       now,
                                       typeof(TradeBar),
                                       symbol,
                                       resolution,
                                       SecurityExchangeHours.AlwaysOpen(TimeZones.Utc),
                                       DateTimeZone.Utc,
                                       Resolution.Minute,
                                       false,
                                       false,
                                       DataNormalizationMode.Adjusted,
                                       TickType.Trade)
                };

                var history = historyProvider.GetHistory(requests, TimeZones.Utc).ToList();

                Log.Trace("Data points retrieved: " + historyProvider.DataPointCount);
                Assert.AreEqual(0, historyProvider.DataPointCount);
                Assert.IsEmpty(history);
            };

            Assert.DoesNotThrow(test);
        }

        private static TestCaseData[] ValidHistory
        {
            get
            {
                return new[]
                {
                    // valid
                    new TestCaseData(Symbol.Create("ETHUSD", SecurityType.Crypto, Market.Kraken), Resolution.Tick, Time.OneHour, false),
                    new TestCaseData(Symbol.Create("ETHUSD", SecurityType.Crypto, Market.Kraken), Resolution.Minute, Time.OneDay, false),
                    new TestCaseData(Symbol.Create("ETHUSD", SecurityType.Crypto, Market.Kraken), Resolution.Hour, TimeSpan.FromDays(30), false),
                    new TestCaseData(Symbol.Create("ETHUSD", SecurityType.Crypto, Market.Kraken), Resolution.Daily, TimeSpan.FromDays(15), false),
                };
            }
        }

        private static TestCaseData[] NoHistory
        {
            get
            {
                return new[]
                {
                    new TestCaseData(Symbol.Create("ETHUSD", SecurityType.Crypto, Market.Kraken), Resolution.Second, Time.OneMinute),
                    
                    // invalid security type, no error, empty result"
                    new TestCaseData(Symbols.AAPL, Resolution.Daily, TimeSpan.FromDays(15)),
                    
                    // invalid period, no error, empty result
                    new TestCaseData(Symbols.EURUSD, Resolution.Daily, TimeSpan.FromDays(-15)),
                };
            }
        }

        private static TestCaseData[] InvalidHistory
        {
            get
            {
                return new[]
                {
                    // invalid symbol, throws "System.ArgumentException : Unknown symbol: XYZ"
                    new TestCaseData(Symbol.Create("XYZ", SecurityType.Crypto, Market.Kraken), Resolution.Daily, TimeSpan.FromDays(15), true)
                };
            }
        }
    }
}
