/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2017 QuantConnect Corporation.
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
using System.Globalization;
using System.Linq;
using QuantConnect.Data;
using System.Net;
using Newtonsoft.Json;
using NodaTime;
using QuantConnect.Brokerages.Kraken;
using QuantConnect.Configuration;
using QuantConnect.Data.Market;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.ToolBox.KrakenDownloader
{
    /// <summary>
    /// Kraken Data Downloader class
    /// </summary>
    public class KrakenDataDownloader : IDataDownloader
    {
        private readonly KrakenBrokerage _brokerage;
        private readonly KrakenSymbolMapper _symbolMapper = new ();
        private const string UrlPrototype = @"https://api.kraken.com/0/public/Trades?pair={0}&since={1}";

        public KrakenDataDownloader()
        {
            var tier = Config.Get("kraken-verification-tier", "Starter");
            _brokerage = new KrakenBrokerage(null, null, tier, 10, null, null, null);
        }

        /// <summary>
        /// Get historical data enumerable for a trading pair, type and resolution given this start and end time (in UTC).
        /// </summary>
        /// <param name="symbol">Symbol for the data we're looking for.</param>
        /// <param name="resolution">Resolution of the data request</param>
        /// <param name="startUtc">Start time of the data in UTC</param>
        /// <param name="endUtc">End time of the data in UTC</param>
        /// <returns>Enumerable of base data for this symbol</returns>
        public IEnumerable<BaseData> Get(Symbol symbol, Resolution resolution, DateTime startUtc, DateTime endUtc)
        {
            if (endUtc < startUtc)
            {
                throw new ArgumentException("The end date must be greater or equal than the start date.");
            }

            if (!_symbolMapper.IsKnownLeanSymbol(symbol))
            {
                throw new ArgumentException($"The ticker {symbol.Value} is not available in Kraken. Use Lean symbols for downloader (i.e BTCUSD, not XXBTZUSD)");
            }

            var historyRequest = new HistoryRequest(
                startUtc,
                endUtc,
                typeof(TradeBar),
                symbol,
                resolution,
                SecurityExchangeHours.AlwaysOpen(TimeZones.Utc),
                DateTimeZone.Utc,
                resolution,
                false,
                false,
                DataNormalizationMode.Adjusted,
                TickType.Trade);

            foreach (var baseData in _brokerage.GetHistory(historyRequest))
            {
                yield return baseData;
            }
           
        }
        
        
        /// <summary>
        /// Aggregates a list of minute bars at the requested resolution
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="bars"></param>
        /// <param name="resolution"></param>
        /// <returns></returns>
        internal IEnumerable<TradeBar> AggregateBars(Symbol symbol, IEnumerable<Tick> bars, TimeSpan resolution)
        {
            return
                (from b in bars
                    group b by b.Time.RoundDown(resolution)
                    into g
                    select new TradeBar
                    {
                        Symbol = symbol,
                        Time = g.Key,
                        Open = g.First().Value,
                        High = g.Max(i => i.Value),
                        Low = g.Min(b => b.Value),
                        Close = g.Last().Value,
                        Volume = g.Sum(b => b.Quantity),
                        Value = g.Last().Value,
                        DataType = MarketDataType.TradeBar,
                        Period = resolution,
                        EndTime = g.Key.AddMilliseconds(resolution.TotalMilliseconds)
                    });
        }
    }
}
