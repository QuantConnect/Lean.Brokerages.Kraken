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
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Kraken
{
    /// <summary>
    /// Factory type for the <see cref="KrakenBrokerage"/>
    /// </summary>
    public class KrakenBrokerageFactory : BrokerageFactory
    {
        /// <summary>
        /// Factory constructor
        /// </summary>
        public KrakenBrokerageFactory()
            : base(typeof(KrakenBrokerage))
        {
        }

        /// <summary>
        /// Dispose 
        /// </summary>
        public override void Dispose()
        {
            
        }

        /// <summary>
        /// provides brokerage connection data
        /// </summary>
        public override Dictionary<string, string> BrokerageData => new Dictionary<string, string>
        {
            { "kraken-api-secret", Config.Get("kraken-api-secret")},
            { "kraken-api-key", Config.Get("kraken-api-key")},
            { "kraken-verification-tier", Config.Get("kraken-verification-tier")},
            { "kraken-orderbook-depth", Config.Get("kraken-orderbook-depth", "10")},

            // load holdings if available
            { "live-holdings", Config.Get("live-holdings")},
        };
        
        /// <summary>
        /// The brokerage model
        /// </summary>
        /// <param name="orderProvider">The order provider</param>
        public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider) => new KrakenBrokerageModel();

        /// <summary>
        /// Create the Kraken Brokerage instance
        /// </summary>
        /// <param name="algorithm"><see cref="IAlgorithm"/> instance</param>
        /// <param name="job">Lean <see cref="LiveNodePacket"/></param>
        /// <returns>Instance of <see cref="KrakenBrokerage"/></returns>
        public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
        {
            var required = new[] { "kraken-api-secret", "kraken-api-key", "kraken-verification-tier" };

            foreach (var item in required)
            {
                if (string.IsNullOrEmpty(job.BrokerageData[item]))
                {
                    throw new ArgumentException($"KrakenBrokerageFactory.CreateBrokerage: Missing {item} in config.json");
                }
            }

            if (!job.BrokerageData.TryGetValue("kraken-orderbook-depth", out var orderDepth))
            {
                orderDepth = BrokerageData["kraken-orderbook-depth"];
            }

            var brokerage = new KrakenBrokerage(
                job.BrokerageData["kraken-api-key"],
                job.BrokerageData["kraken-api-secret"],
                job.BrokerageData["kraken-verification-tier"],
                orderDepth.ToInt32(),
                algorithm,
                Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager"), forceTypeNameOnExisting: false),
                job);
            Composer.Instance.AddPart<IDataQueueHandler>(brokerage);

            return brokerage;
        }
    }
}
