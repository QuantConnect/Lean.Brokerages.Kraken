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
            _brokerage = new KrakenBrokerage(null, null, tier, null, null, null);
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

            if (resolution == Resolution.Tick)
            {
                foreach (var baseData in TradesDownload(symbol, startUtc, endUtc))
                {
                    yield return baseData;
                }
                yield break;
            }

            if (resolution == Resolution.Second)
            {
                throw new ArgumentException("Kraken does not support seconds resolution.");
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
        /// Download trades (TradeBars with resolution tick)
        /// </summary>
        /// <param name="symbol">Symbol for the data we're looking for.</param>
        /// <param name="startUtc">Start time of the data in UTC</param>
        /// <param name="endUtc">End time of the data in UTC</param>
        /// <returns>Enumerable of base data for this symbol</returns>
        private IEnumerable<BaseData> TradesDownload(Symbol symbol, DateTime startUtc, DateTime endUtc)
        {
            var startUnixTime = Convert.ToInt64(Time.DateTimeToUnixTimeStamp(startUtc) * 1000000000); // Multiply by 10^9 per Kraken API
            var endUnixTime = Convert.ToInt64(Time.DateTimeToUnixTimeStamp(endUtc) * 1000000000);
            var marketTicker = _symbolMapper.GetBrokerageSymbol(symbol);
            var url = string.Format(CultureInfo.InvariantCulture, UrlPrototype, marketTicker, startUnixTime);
            List<List<string>> data;

            using (var client = new WebClient())
            {
                var rateGate = new RateGate(15, TimeSpan.FromMinutes(1)); // 15 calls per minute for Kraken API

                rateGate.WaitToProceed();
                var response = client.DownloadString(url);
                dynamic result = JsonConvert.DeserializeObject<dynamic>(response);
                if (result.error.Count != 0)
                {
                    throw new Exception("Error in Kraken API: " + result.error[0]);
                }

                if (result.result.ContainsKey(marketTicker))
                {
                    data = result.result[marketTicker].ToObject<List<List<string>>>();
                }
                else
                {
                    throw new NotSupportedException("Asset pair was not found in the response. Make sure you use the correct model (XBTUSD -> XXBTZUSD).");
                }

                foreach (var i in data)
                {
                    var time = Time.UnixTimeStampToDateTime(Parse.Double(i[2].Split('.')[0]));
                    if (time > endUtc)
                    {
                        break;
                    }

                    var value = Parse.Decimal(i[0]);
                    var volume = Parse.Decimal(i[1]);

                    yield return new Tick
                    {
                        Value = value,
                        Time = time,
                        DataType = MarketDataType.Tick,
                        Symbol = symbol,
                        TickType = TickType.Trade,
                        Quantity = volume,
                        Exchange = Market.Kraken
                    };
                }

                var last = Convert.ToInt64(result.result.last);
                while (last < endUnixTime)
                {
                    url = string.Format(UrlPrototype, marketTicker, last);

                    rateGate.WaitToProceed();
                    response = client.DownloadString(url);
                    result = JsonConvert.DeserializeObject<dynamic>(response);

                    var errorCount = 0;
                    while (result.error.Count != 0 && errorCount < 10)
                    {
                        errorCount++;
                        rateGate.WaitToProceed();
                        response = client.DownloadString(url);
                        result = JsonConvert.DeserializeObject<dynamic>(response);
                    }

                    if (result.error.Count != 0 && errorCount >= 10)
                    {
                        throw new Exception("Error in Kraken API: " + result.error[0]);
                    }

                    data = result.result[marketTicker].ToObject<List<List<string>>>();

                    foreach (var i in data)
                    {
                        var time = Time.UnixTimeStampToDateTime(Parse.Double(i[2].Split('.')[0]));
                        if (time > endUtc)
                        {
                            break;
                        }

                        var value = Parse.Decimal(i[0]);
                        var volume = Parse.Decimal(i[1]);

                        yield return new Tick
                        {
                            Value = value,
                            Time = time,
                            DataType = MarketDataType.Tick,
                            Symbol = symbol,
                            TickType = TickType.Trade,
                            Quantity = volume,
                            Exchange = Market.Kraken
                        };
                    }

                    last = Convert.ToInt64(result.result.last);
                }
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
