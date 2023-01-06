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
using System.Collections.Generic;
using System.Linq;
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
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Securities.Crypto;
using QuantConnect.Tests.Common.Securities;
using QuantConnect.Util;

namespace QuantConnect.Tests.Brokerages.Kraken
{
    public partial class KrakenBrokerageTests : BrokerageTests
    {

        protected override IBrokerage CreateBrokerage(IOrderProvider orderProvider, ISecurityProvider securityProvider)
        {
            // avoid rate limit hits
            Thread.Sleep(1000);

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

            if (environment.Contains("KRAKEN_PUBLIC"))
            {
                Config.Set("kraken-api-key", environment["KRAKEN_PUBLIC"].ToString());
            }
            else
            {
                Log.Error("Environment doesn't have KRAKEN_PUBLIC variable");
            }
            
            if (environment.Contains("KRAKEN_PRIVATE"))
            {
                Config.Set("kraken-api-secret", environment["KRAKEN_PRIVATE"].ToString());
            }
            else
            {
                Log.Error("Environment doesn't have KRAKEN_PRIVATE variable");
            }
            
            var apiKey = Config.Get("kraken-api-key");
            var apiSecret = Config.Get("kraken-api-secret");
            var tier = Config.Get("kraken-verification-tier");
            var orderBookDepth = Config.GetInt("kraken-orderbook-depth", 10);


            return new KrakenBrokerage(apiKey, apiSecret, tier, orderBookDepth, algorithm.Object, new AggregationManager(), null);
        }

        protected override Symbol Symbol => StaticSymbol;

        private static Symbol StaticSymbol => Symbol.Create("BTCUSD", SecurityType.Crypto, Market.Kraken);

        public static TestCaseData[] OrderParameters => new[]
        {
            new TestCaseData(new MarketOrderTestParameters(StaticSymbol)).SetName("MarketOrder"),
            new TestCaseData(new LimitOrderTestParameters(StaticSymbol, 70000, 30000)).SetName("LimitOrder"),
            new TestCaseData(new StopLimitOrderTestParameters(StaticSymbol, 70000, 30000)).SetName("StopLimitOrder"),
            new TestCaseData(new StopMarketOrderTestParameters(StaticSymbol, 70000, 30000)).SetName("StopMarketOrder"),
            new TestCaseData(new LimitIfTouchedOrderTestParameters(StaticSymbol, 70000, 30000)).SetName("LimitIfTouchedOrder"),
        };

        public static TestCaseData[] NonUpdatableOrderParameters => new[]
        {
            new TestCaseData(new MarketOrderTestParameters(StaticSymbol)).SetName("MarketOrder"),
            new TestCaseData(new NonUpdateableLimitOrderTestParameters(StaticSymbol, 70000, 30000)).SetName("LimitOrder"),
            new TestCaseData(new NonUpdateableStopLimitOrderTestParameters(StaticSymbol, 70000, 30000)).SetName("StopLimitOrder"),
            new TestCaseData(new NonUpdateableStopMarketOrderTestParameters(StaticSymbol, 70000, 30000)).SetName("StopMarketOrder"),
            new TestCaseData(new NonUpdateableLimitIfTouchedOrderTestParameters(StaticSymbol, 70000, 30000)).SetName("LimitIfTouchedOrder")
        };
        
        public static TestCaseData[] CancelOrderParameters => new[] // without market
        {
            new TestCaseData(new LimitOrderTestParameters(StaticSymbol, 70000, 30000)).SetName("LimitOrder"),
            new TestCaseData(new StopLimitOrderTestParameters(StaticSymbol, 70000, 30000)).SetName("StopLimitOrder"),
            new TestCaseData(new StopMarketOrderTestParameters(StaticSymbol, 70000, 30000)).SetName("StopMarketOrder"),
            new TestCaseData(new LimitIfTouchedOrderTestParameters(StaticSymbol, 70000, 30000)).SetName("LimitIfTouchedOrder"),
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

        protected override decimal GetDefaultQuantity() => SymbolPropertiesDatabase.FromDataFolder().GetSymbolProperties(Market.Kraken, Symbol, Symbol.SecurityType, Currencies.USD).MinimumOrderSize.Value; // Take order minimum

        [Explicit("This test requires a configured and testable account")]
        [Test, TestCaseSource(nameof(CancelOrderParameters))]
        public override void CancelOrders(OrderTestParameters parameters)
        {
            base.CancelOrders(parameters);
        }

        [Explicit("This test requires a configured and testable account")]
        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void LongFromZero(OrderTestParameters parameters)
        {
            base.LongFromZero(parameters);
        }

        [Explicit("This test requires a configured and testable account")]
        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void CloseFromLong(OrderTestParameters parameters)
        {
            base.CloseFromLong(parameters);
        }

        [Explicit("This test requires a configured and testable account")]
        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void ShortFromZero(OrderTestParameters parameters)
        {
            base.ShortFromZero(parameters);
        }

        [Explicit("This test requires a configured and testable account")]
        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void CloseFromShort(OrderTestParameters parameters)
        {
            base.CloseFromShort(parameters);
        }

        [Explicit("This test requires a configured and testable account")]
        [Test, TestCaseSource(nameof(NonUpdatableOrderParameters))]
        public override void ShortFromLong(OrderTestParameters parameters)
        {
            base.ShortFromLong(parameters);
        }

        [Explicit("This test requires a configured and testable account")]
        [Test, TestCaseSource(nameof(NonUpdatableOrderParameters))]
        public override void LongFromShort(OrderTestParameters parameters)
        {
            base.LongFromShort(parameters);
        }

        [Test, Description("In Kraken AccountHoldings would always return 0 as long as leverage 1")]
        public override void GetAccountHoldings()
        {
            Log.Trace("");
            Log.Trace("GET ACCOUNT HOLDINGS");
            Log.Trace("");
            var before = Brokerage.GetAccountHoldings();
            Assert.AreEqual(0, before.Count());

            PlaceOrderWaitForStatus(new MarketOrder(Symbol, GetDefaultQuantity(), DateTime.Now));
            Thread.Sleep(2000);

            var after = Brokerage.GetAccountHoldings();
            Assert.AreEqual(0, after.Count());
        }

        [Test]
        public void OpenClosePositionTest()
        {
            var security = SecurityInit(out var model, out var securities);
            var transactions = new SecurityTransactionManager(null, securities);
            transactions.SetOrderProcessor(new FakeOrderProcessor());

            var portfolio = new SecurityPortfolioManager(securities, transactions);
            portfolio.CashBook["BTC"] = new Cash("BTC", 0.1m, 1);

            var initialStateDict = new Dictionary<string, decimal>();
            foreach (var kvp in portfolio.CashBook)
            {
                initialStateDict.Add(kvp.Key, kvp.Value.Amount);
            }
            
            // Open
            model.ProcessFill(portfolio, security, new OrderEvent(1, Symbol, DateTime.UtcNow, OrderStatus.Filled, OrderDirection.Buy, security.Price, 0.01m, OrderFee.Zero));
            
            foreach (var kvp in portfolio.CashBook)
            {
                var amount = initialStateDict[kvp.Key];
                Assert.AreNotEqual(amount, kvp.Value.Amount);
            }
            
            // Close
            model.ProcessFill(portfolio, security, new OrderEvent(2, Symbol, DateTime.UtcNow, OrderStatus.Filled, OrderDirection.Sell, security.Price, -0.01m, OrderFee.Zero));

            foreach (var kvp in portfolio.CashBook)
            {
                var amount = initialStateDict[kvp.Key];
                Assert.AreEqual(amount, kvp.Value.Amount);
            }
        }
        
        [Test]
        public void ClosePositionTest()
        {
            var security = SecurityInit(out var model, out var securities);

            var holding = new Holding
            {
                Quantity = 0.1m,
                AveragePrice = security.Price,
                Symbol = security.Symbol,
                MarketPrice = security.Price,
                CurrencySymbol = security.QuoteCurrency.CurrencySymbol,
                MarketValue = 0.1m * security.Price,
                UnrealizedPnL = 0
            };
            
            security.Holdings.SetHoldings(holding.AveragePrice, holding.Quantity);

            var transactions = new SecurityTransactionManager(null, securities);
            transactions.SetOrderProcessor(new FakeOrderProcessor());

            var portfolio = new SecurityPortfolioManager(securities, transactions);
            portfolio.CashBook["BTC"] = new Cash("BTC", 0.1m, 1);

            var initialStateDict = new Dictionary<string, decimal>();
            foreach (var kvp in portfolio.CashBook)
            {
                initialStateDict.Add(kvp.Key, kvp.Value.Amount);
            }

            // Close
            model.ProcessFill(portfolio, security, new OrderEvent(2, Symbol, DateTime.UtcNow, OrderStatus.Filled, OrderDirection.Sell, security.Price, -0.1m, OrderFee.Zero));

            foreach (var kvp in portfolio.CashBook)
            {
                var amount = initialStateDict[kvp.Key];
                Assert.AreNotEqual(amount, kvp.Value.Amount);
            }
        }

        private Crypto SecurityInit(out SecurityPortfolioModel model, out SecurityManager securities)
        {
            CurrencyPairUtil.DecomposeCurrencyPair(Symbol, out var baseCurrency, out var quoteCurrency);
            var security = new Crypto(
                SecurityExchangeHours.AlwaysOpen(TimeZones.NewYork),
                new Cash(quoteCurrency, 100, 1m),
                new Cash(baseCurrency, 0, 0),
                new SubscriptionDataConfig(
                    typeof(TradeBar),
                    Symbol,
                    Resolution.Minute,
                    TimeZones.NewYork,
                    TimeZones.NewYork,
                    false,
                    false,
                    false
                ),
                SymbolProperties.GetDefault(Currencies.USD),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );

            security.BuyingPowerModel = new SecurityMarginModel(2);
            model = new SecurityPortfolioModel();

            security.FeeModel = new KrakenFeeModel();
            securities = new SecurityManager(new TimeKeeper(DateTime.UtcNow, TimeZones.NewYork))
            {
                {Symbol, security}
            };
            var brokerage = (KrakenBrokerage) Brokerage;
            security.Update(new[] {brokerage.GetTick(Symbol)}, typeof(Tick));
            return security;
        }
    }
}
