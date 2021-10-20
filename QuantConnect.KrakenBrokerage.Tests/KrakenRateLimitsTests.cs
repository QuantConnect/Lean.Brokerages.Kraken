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
using System.Threading.Tasks;
using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Brokerages.Kraken;
using QuantConnect.Logging;

namespace QuantConnect.Tests.Brokerages.Kraken
{
    public class KrakenRateLimitsTests
    {
        [Test]
        [TestCaseSource(nameof(RestLimits))]
        public void RestRateLimitTest(string tier, int requestNumber, bool shouldExceed)
        {
            Log.Trace("");
            Log.Trace("Rest Rate Limit test");
            Log.Trace("");
            
            var rateLimiter = new TestKrakenBrokerageRateLimits(tier);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            
            for (int i = 0; i < requestNumber; i++)
            {
                rateLimiter.RateLimitCheck();
            }

            watch.Stop();
            if (shouldExceed)
            {
                Assert.Greater(watch.ElapsedMilliseconds, 19000);
            }
            else
            {
                Assert.Less(watch.ElapsedMilliseconds, 20000);
                Assert.AreEqual(requestNumber, rateLimiter.GetRateLimitCounter);
            }
        }
        
        [Test]
        [TestCaseSource(nameof(OrderLimits))]
        public void OrderLimitTest(string tier, int requestNumber, bool shouldThrow)
        {
            Log.Trace("");
            Log.Trace("Order Limit test");
            Log.Trace("");
            var rateLimiter = new KrakenBrokerageRateLimits(tier);

            TestDelegate test = () =>
            {
                for (int i = 0; i < requestNumber; i++)
                {
                    rateLimiter.OrderRateLimitCheck("XXBTZUSD");
                }
            };
            
            
            if (shouldThrow)
            {
                Assert.Throws<BrokerageException>(test);
            }
            else
            {
                Assert.DoesNotThrow(test);
            }
        }
        
        [Test]
        [TestCaseSource(nameof(CancelLimits))]
        public void CancelOrderLimitTest(string tier, int requestNumber, int secondsOrderPlacedAgo, bool shouldExceed)
        {
            Log.Trace("");
            Log.Trace("Cancel Order Limit test");
            Log.Trace("");
            var rateLimiter = new KrakenBrokerageRateLimits(tier);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            
            for (int i = 0; i < requestNumber; i++)
            {
                rateLimiter.CancelOrderRateLimitCheck("XXBTZUSD", DateTime.UtcNow - TimeSpan.FromSeconds(secondsOrderPlacedAgo));
            }

            watch.Stop();
            if (shouldExceed)
            {
                Assert.Greater(watch.ElapsedMilliseconds, 19000);
            }
            else
            {
                Assert.Less(watch.ElapsedMilliseconds, 20000);
            }
        }
        
        [Test]
        [TestCaseSource(nameof(RestDecayLimits))]
        public async Task RestDecayLimitTest(string tier, int decaySeconds, decimal multiplier)
        {
            Log.Trace("");
            Log.Trace("Rest Decay Limit test");
            Log.Trace("");
            
            var rateLimiter = new TestKrakenBrokerageRateLimits(tier);

            const int requestNumber = 15;
            
            for (int i = 0; i < requestNumber; i++)
            {
                rateLimiter.RateLimitCheck();
            }

            await Task.Delay(decaySeconds * 1000 + 500); // 500ms so update timer have time to run

            Assert.AreEqual(rateLimiter.GetRateLimitCounter, requestNumber - decaySeconds * multiplier);
        }
        
        [Test]
        [TestCaseSource(nameof(CancelDecayLimits))]
        public async Task CancelDecayLimitTest(string tier, int decaySeconds, int requestNumber, bool shouldExceed)
        {
            Log.Trace("");
            Log.Trace("Cancel Decay Limit test");
            Log.Trace("");
            
            var rateLimiter = new KrakenBrokerageRateLimits(tier);
            
            for (int i = 0; i < requestNumber; i++)
            {
                // process weight 8 every request
                rateLimiter.CancelOrderRateLimitCheck("XXBTZUSD", DateTime.UtcNow - TimeSpan.FromSeconds(1));
            }

            await Task.Delay(decaySeconds * 1000 + 500); // 500ms so update timer have time to run

            var watch = System.Diagnostics.Stopwatch.StartNew();

            rateLimiter.CancelOrderRateLimitCheck("XXBTZUSD", DateTime.UtcNow - TimeSpan.FromSeconds(1));
            
            watch.Stop();

            if (shouldExceed)
            {
                Assert.Greater(watch.ElapsedMilliseconds, 19000);
            }
            else
            {
                Assert.Less(watch.ElapsedMilliseconds, 20000);
            }
        }
        
        private static TestCaseData[] RestLimits
        {
            get
            {
                return new[]
                {
                    new TestCaseData("Starter", 14, false), // limit - 15
                    new TestCaseData("Starter", 16, true),
                    new TestCaseData("Pro", 19, false), // Pro and Intermediate have same rate limits here - 20
                    new TestCaseData("Pro", 21, true), // Pro and Intermediate have same rate limits here - 20
                };
            }
        }
        
        private static TestCaseData[] OrderLimits
        {
            get
            {
                return new[]
                {
                    new TestCaseData("Starter", 59, false), // limit - 60
                    new TestCaseData("Starter", 61, true),
                    new TestCaseData("Intermediate", 79, false), // limit - 80
                    new TestCaseData("Intermediate", 81, true),
                    new TestCaseData("Pro", 224, false), // limit - 225
                    new TestCaseData("Pro", 226, true), 
                };
            }
        }
        
        private static TestCaseData[] CancelLimits
        {
            get
            {
                return new[]
                {
                    new TestCaseData("Starter", 7, 1, false), // limit - 60, weight - 8
                    new TestCaseData("Starter", 8, 1, true),
                    new TestCaseData("Starter", 9, 7, false), // limit - 60, weight - 6
                    new TestCaseData("Starter", 11, 7, true),
                    new TestCaseData("Starter", 11, 12, false), // limit - 60, weight - 5
                    new TestCaseData("Starter", 13, 12, true),
                    new TestCaseData("Starter", 14, 30, false), // limit - 60, weight - 4
                    new TestCaseData("Starter", 16, 30, true),
                    new TestCaseData("Starter", 29, 75, false), // limit - 60, weight - 2
                    new TestCaseData("Starter", 31, 75, true),
                    new TestCaseData("Starter", 59, 100, false), // limit - 60, weight - 1
                    new TestCaseData("Starter", 61, 100, true),
                    new TestCaseData("Starter", 100, 1000, false), // limit - 60, weight - 0, unlimited
                    
                    new TestCaseData("Intermediate", 15, 1, false), // limit - 125, weight - 8
                    new TestCaseData("Intermediate", 16, 1, true),
                    new TestCaseData("Intermediate", 20, 7, false), // limit - 125, weight - 6
                    new TestCaseData("Intermediate", 21, 7, true),
                    new TestCaseData("Intermediate", 24, 12, false), // limit - 125, weight - 5
                    new TestCaseData("Intermediate", 26, 12, true),
                    new TestCaseData("Intermediate", 31, 30, false), // limit - 125, weight - 4
                    new TestCaseData("Intermediate", 32, 30, true),
                    new TestCaseData("Intermediate", 62, 75, false), // limit - 125, weight - 2
                    new TestCaseData("Intermediate", 63, 75, true),
                    new TestCaseData("Intermediate", 124, 100, false), // limit - 125, weight - 1
                    new TestCaseData("Intermediate", 126, 100, true),
                    new TestCaseData("Intermediate", 200, 1000, false), // limit - 125, weight - 0, unlimited
                    
                    
                    new TestCaseData("Pro", 22, 1, false), // limit - 180, weight - 8
                    new TestCaseData("Pro", 23, 1, true),
                    new TestCaseData("Pro", 29, 7, false), // limit - 180, weight - 6
                    new TestCaseData("Pro", 31, 7, true),
                    new TestCaseData("Pro", 35, 12, false), // limit - 180, weight - 5
                    new TestCaseData("Pro", 37, 12, true),
                    new TestCaseData("Pro", 44, 30, false), // limit - 180, weight - 4
                    new TestCaseData("Pro", 46, 30, true),
                    new TestCaseData("Pro", 89, 75, false), // limit - 180, weight - 2
                    new TestCaseData("Pro", 91, 75, true),
                    new TestCaseData("Pro", 179, 100, false), // limit - 180, weight - 1
                    new TestCaseData("Pro", 181, 100, true),
                    new TestCaseData("Pro", 200, 1000, false), // limit - 180, weight - 0, unlimited
                };
            }
        }
        
        private static TestCaseData[] RestDecayLimits
        {
            get
            {
                return new[]
                {
                    new TestCaseData("Starter",  5, 0.33m), // decay per second 0.33
                    new TestCaseData("Starter", 2, 0.33m),
                    new TestCaseData("Intermediate", 5, 0.5m), // decay per second 0.5
                    new TestCaseData("Intermediate", 2, 0.5m),
                    new TestCaseData("Pro", 5, 1m), // decay per second 1
                    new TestCaseData("Pro", 2, 1m),
                };
            }
        }
        
        private static TestCaseData[] CancelDecayLimits
        {
            get
            {
                return new[]
                {
                    new TestCaseData("Starter", 0, 7, true), // decay per second 1
                    new TestCaseData("Starter", 8, 7, false), // 8 * 1 = -8
                    new TestCaseData("Intermediate", 0, 15, true), // decay per second 2.34
                    new TestCaseData("Intermediate", 4, 15, false), // 4 * 2.34 = -9.36
                    new TestCaseData("Pro", 0, 22, true), // decay per second 3.75
                    new TestCaseData("Pro", 3, 22, false), // 3 * 3.75 = -11.25
                };
            }
        }

        private class TestKrakenBrokerageRateLimits : KrakenBrokerageRateLimits
        {
            public decimal GetRateLimitCounter => RateLimitCounter;
            public TestKrakenBrokerageRateLimits(string verificationTier) : base(verificationTier)
            {
            }
        }
    }
}