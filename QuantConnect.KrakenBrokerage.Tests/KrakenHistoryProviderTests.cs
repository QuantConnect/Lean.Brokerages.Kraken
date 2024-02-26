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
using QuantConnect.Logging;
using QuantConnect.Securities;

namespace QuantConnect.Tests.Brokerages.Kraken
{
    public partial class KrakenBrokerageTests
    {
        private static Symbol _ethusd;
        private static Symbol ETHUSD
        {
            get
            {
                if (_ethusd == null)
                {
                    TestGlobals.Initialize();
                    _ethusd = Symbol.Create("ETHUSD", SecurityType.Crypto, Market.Kraken);
                }

                return _ethusd;
            }
        }

        [Test]
        [TestCaseSource(nameof(ValidHistory))]
        [TestCaseSource(nameof(InvalidHistory))]
        public void GetsHistory(Symbol symbol, Resolution resolution, TickType tickType, TimeSpan period, bool unsupported)
        {
            var brokerage = unsupported ? TestableKrakenBrokerage.Create() : (KrakenBrokerage)Brokerage;

            var now = DateTime.UtcNow;
            var request = new HistoryRequest(now.Add(-period),
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
                tickType);

            var history = brokerage.GetHistory(request)?.ToList();

            if (unsupported)
            {
                Assert.IsNull(history);
                return;
            }

            Assert.IsNotNull(history);

            foreach (var bar in history.Cast<TradeBar>())
            {
                Log.Debug($"{bar.Time}: {bar}");
            }

            Log.Trace("Data points retrieved: " + history.Count);
        }

        private static TestCaseData[] ValidHistory
        {
            get
            {
                return new[]
                {
                    // valid
                    new TestCaseData(ETHUSD, Resolution.Tick, TickType.Trade, Time.OneHour, false),
                    new TestCaseData(ETHUSD, Resolution.Minute, TickType.Trade, Time.OneDay, false),
                    new TestCaseData(ETHUSD, Resolution.Hour, TickType.Trade, TimeSpan.FromDays(30), false),
                    new TestCaseData(ETHUSD, Resolution.Daily, TickType.Trade, TimeSpan.FromDays(15), false),
                };
            }
        }

        private static TestCaseData[] InvalidHistory
        {
            get
            {
                TestGlobals.Initialize();
                return new[]
                {
                    // invalid symbol
                    new TestCaseData(Symbol.Create("XYZ", SecurityType.Crypto, Market.Kraken), Resolution.Daily, TickType.Trade, TimeSpan.FromDays(15), true),

                    // invalid security type
                    new TestCaseData(Symbols.AAPL, Resolution.Daily, TickType.Trade, TimeSpan.FromDays(15), true),

                    // invalid resolution
                    new TestCaseData(ETHUSD, Resolution.Second, TickType.Trade, TimeSpan.FromDays(15), true),

                    // invalid tick type
                    new TestCaseData(ETHUSD, Resolution.Daily, TickType.Quote, TimeSpan.FromDays(15), true),
                    new TestCaseData(ETHUSD, Resolution.Daily, TickType.OpenInterest, TimeSpan.FromDays(15), true),

                    // invalid period
                    new TestCaseData(ETHUSD, Resolution.Daily, TickType.Trade, TimeSpan.FromDays(-15), true),

                };
            }
        }

        private class TestableKrakenBrokerage : KrakenBrokerage
        {
            public TestableKrakenBrokerage()
            {
            }

            public override void Connect()
            {
            }

            public static TestableKrakenBrokerage Create()
            {
                var brokerage = new TestableKrakenBrokerage();

                var factory = new KrakenBrokerageFactory();
                brokerage.SetJob(new Packets.LiveNodePacket() { BrokerageData = factory.BrokerageData });

                return brokerage;
            }
        }
    }
}
