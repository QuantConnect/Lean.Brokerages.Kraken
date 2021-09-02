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
using System.Threading;
using Moq;
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.Kraken;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Logging;
using QuantConnect.Securities;
using QuantConnect.Tests.Common.Securities;

namespace QuantConnect.Tests.Brokerages.Kraken
{
    public partial class KrakenBrokerageTests : BrokerageTests
    {
        
        protected override IBrokerage CreateBrokerage(IOrderProvider orderProvider, ISecurityProvider securityProvider)
        {
            var securities = new SecurityManager(new TimeKeeper(DateTime.UtcNow, TimeZones.NewYork))
            {
                { Symbol, CreateSecurity(Symbol) }
            };

            var transactions = new SecurityTransactionManager(null, securities);
            transactions.SetOrderProcessor(new FakeOrderProcessor());

            var algorithm = new Mock<IAlgorithm>();
            algorithm.Setup(a => a.Transactions).Returns(transactions);
            algorithm.Setup(a => a.BrokerageModel).Returns(new KrakenBrokerageModel());
            algorithm.Setup(a => a.Portfolio).Returns(new SecurityPortfolioManager(securities, transactions));

            var apiKey = Config.Get("kraken-api-key");
            var apiSecret = Config.Get("kraken-api-secret");
            var tier = Config.Get("kraken-verification-tier");
            

            return new KrakenBrokerage(apiKey, apiSecret, tier, algorithm.Object, new AggregationManager(), null);
        }

        protected override Symbol Symbol => StaticSymbol;
        
        private static Symbol StaticSymbol => Symbol.Create("ETHUSD", SecurityType.Crypto, Market.Bitfinex);
        protected override SecurityType SecurityType { get; }
        protected override bool IsAsync()
        {
            throw new System.NotImplementedException();
        }

        protected override decimal GetAskPrice(Symbol symbol)
        {
            throw new System.NotImplementedException();
        }
        
        [Test]
        public void GetsTickData()
        {
            var cancelationToken = new CancellationTokenSource();
            var brokerage = (KrakenBrokerage)Brokerage;

            var configs = new SubscriptionDataConfig[] {
                GetSubscriptionDataConfig<QuoteBar>(Symbol.Create("EURUSD", SecurityType.Crypto, Market.Kraken), Resolution.Tick),
                GetSubscriptionDataConfig<QuoteBar>(Symbol.Create("BTCUSD", SecurityType.Crypto, Market.Kraken), Resolution.Tick),
                GetSubscriptionDataConfig<QuoteBar>(Symbol.Create("ETHBTC", SecurityType.Crypto, Market.Kraken), Resolution.Tick),
            };

            foreach (var config in configs)
            {
                ProcessFeed(
                    brokerage.Subscribe(config, (s, e) => { }),
                    cancelationToken,
                    (tick) => {
                        if (tick != null)
                        {
                            Log.Trace("{0}: {1} - {2} / {3}", tick.Time.ToStringInvariant("yyyy-MM-dd HH:mm:ss.fff"), tick.Symbol, (tick as Tick)?.BidPrice, (tick as Tick)?.AskPrice);
                        }
                    });
            }

            Thread.Sleep(20000);

            foreach (var config in configs)
            {
                brokerage.Unsubscribe(config);
                Thread.Sleep(20000);
            }

            cancelationToken.Cancel();
        }
    }
}
