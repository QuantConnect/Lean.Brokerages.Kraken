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
using Moq;
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.Kraken;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Tests.Common.Securities;

namespace QuantConnect.Tests.Brokerages.Kraken
{
    public partial class KrakenBrokerageTests : BrokerageTests
    {

        protected override IBrokerage CreateBrokerage(IOrderProvider orderProvider, ISecurityProvider securityProvider)
        {
            var environment = Environment.GetEnvironmentVariables();
            
            var securities = new SecurityManager(new TimeKeeper(DateTime.UtcNow, TimeZones.NewYork))
            {
                {Symbol, CreateSecurity(Symbol)}
            };

            var transactions = new SecurityTransactionManager(null, securities);
            transactions.SetOrderProcessor(new FakeOrderProcessor());

            var algorithm = new Mock<IAlgorithm>();
            algorithm.Setup(a => a.Transactions).Returns(transactions);
            algorithm.Setup(a => a.BrokerageModel).Returns(new KrakenBrokerageModel(AccountType.Margin));
            algorithm.Setup(a => a.Portfolio).Returns(new SecurityPortfolioManager(securities, transactions));

            var apiKey = Config.Get("kraken-api-key");
            var apiSecret = Config.Get("kraken-api-secret");
            

            if (string.IsNullOrEmpty(apiKey) && environment.Contains("KRAKEN_PUBLIC"))
            {
                apiKey = environment["KRAKEN_PUBLIC"].ToString();
            }
            
            if (string.IsNullOrEmpty(apiSecret) && environment.Contains("KRAKEN_PRIVATE"))
            {
                apiSecret = environment["KRAKEN_PRIVATE"].ToString();
            }
            

            var tier = Config.Get("kraken-verification-tier");


            return new KrakenBrokerage(apiKey, apiSecret, tier, algorithm.Object, new AggregationManager(), null);
        }

        protected override Symbol Symbol => StaticSymbol;

        private static Symbol StaticSymbol => Symbol.Create("ETHUSD", SecurityType.Crypto, Market.Kraken);

        public static TestCaseData[] OrderParameters => new[]
        {
            new TestCaseData(new MarketOrderTestParameters(StaticSymbol)).SetName("MarketOrder"),
            new TestCaseData(new LimitOrderTestParameters(StaticSymbol, 5000, 100)).SetName("LimitOrder"),
            new TestCaseData(new StopLimitOrderTestParameters(StaticSymbol, 5000, 100)).SetName("StopLimitOrder"),
            new TestCaseData(new StopMarketOrderTestParameters(StaticSymbol, 5000, 100)).SetName("StopMarketOrder"),
            new TestCaseData(new LimitIfTouchedOrderTestParameters(StaticSymbol, 5000, 100)).SetName("LimitIfTouchedOrder"),
        };

        public static TestCaseData[] NonUpdatableOrderParameters => new[]
        {
            new TestCaseData(new MarketOrderTestParameters(StaticSymbol)).SetName("MarketOrder"),
            new TestCaseData(new NonUpdateableLimitOrderTestParameters(StaticSymbol, 5000, 100)).SetName("LimitOrder"),
            new TestCaseData(new NonUpdateableStopLimitOrderTestParameters(StaticSymbol, 5000, 100)).SetName("StopLimitOrder"),
            new TestCaseData(new NonUpdateableStopMarketOrderTestParameters(StaticSymbol, 5000, 100)).SetName("StopMarketOrder"),
            // new TestCaseData(new NonUpdateableLimitIfTouchedOrderTestParameters(StaticSymbol, 5000, 100)).SetName("LimitIfTouchedOrder"), don't exist in current version of Lean repo
        };
        
        public static TestCaseData[] CancelOrderParameters => new[] // without market
        {
            new TestCaseData(new LimitOrderTestParameters(StaticSymbol, 5000, 100)).SetName("LimitOrder"),
            new TestCaseData(new StopLimitOrderTestParameters(StaticSymbol, 5000, 100)).SetName("StopLimitOrder"),
            new TestCaseData(new StopMarketOrderTestParameters(StaticSymbol, 5000, 100)).SetName("StopMarketOrder"),
            new TestCaseData(new LimitIfTouchedOrderTestParameters(StaticSymbol, 5000, 100)).SetName("LimitIfTouchedOrder"),
        };
        
        protected override SecurityType SecurityType => SecurityType.Crypto;
        protected override bool IsAsync() => true;

        protected override bool IsCancelAsync() => true;

        protected override decimal GetAskPrice(Symbol symbol)
        {
            var brokerage = (KrakenBrokerage) Brokerage;
            var tick = brokerage.GetTick(symbol);

            return tick.AskPrice;
        }

        protected override decimal GetDefaultQuantity() => 0.004m; // ETH order minimum

        [Test, TestCaseSource(nameof(CancelOrderParameters))]
        public override void CancelOrders(OrderTestParameters parameters)
        {
            base.CancelOrders(parameters);
        }

        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void LongFromZero(OrderTestParameters parameters)
        {
            base.LongFromZero(parameters);
        }

        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void CloseFromLong(OrderTestParameters parameters)
        {
            base.CloseFromLong(parameters);
        }

        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void ShortFromZero(OrderTestParameters parameters)
        {
            base.ShortFromZero(parameters);
        }

        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void CloseFromShort(OrderTestParameters parameters)
        {
            base.CloseFromShort(parameters);
        }

        [Test, TestCaseSource(nameof(NonUpdatableOrderParameters))]
        public override void ShortFromLong(OrderTestParameters parameters)
        {
            base.ShortFromLong(parameters);
        }

        [Test, TestCaseSource(nameof(NonUpdatableOrderParameters))]
        public override void LongFromShort(OrderTestParameters parameters)
        {
            base.LongFromShort(parameters);
        }
    }
}
