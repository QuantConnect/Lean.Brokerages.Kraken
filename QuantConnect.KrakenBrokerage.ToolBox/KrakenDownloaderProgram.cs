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
using System.Linq;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Logging;
using QuantConnect.ToolBox.KrakenDownloader;
using QuantConnect.Util;

namespace QuantConnect.ToolBox.KrakenDataDownloader
{
    public static class KrakenDownloaderProgram
    {
        /// <summary>
        /// Kraken Downloader Toolbox Project For LEAN Algorithmic Trading Engine.
        /// By @matthewsedam
        /// </summary>
        public static void KrakenDownloader(IList<string> tickers, string resolution, DateTime startDate, DateTime endDate)
        {
            if (resolution.IsNullOrEmpty() || tickers.IsNullOrEmpty())
            {
                Console.WriteLine("KrakenDownloader ERROR: '--tickers=' or '--resolution=' parameter is missing");
                Console.WriteLine("--tickers=eg BTCUSD,ETHEUR");
                Console.WriteLine("--resolution=Minute/Hour/Daily/Second/Tick");
                Environment.Exit(1);
            }

            try
            {
                var castResolution = (Resolution)Enum.Parse(typeof(Resolution), resolution);

                // Load settings from config.json and create downloader
                var dataDirectory = Globals.DataFolder;
                var downloader = new KrakenDownloader.KrakenDataDownloader();

                foreach (var pair in tickers)
                {
                    // Download data
                    var pairObject = Symbol.Create(pair, SecurityType.Crypto, Market.Kraken);
                    var data = downloader.Get(new DataDownloaderGetParameters(pairObject, castResolution == Resolution.Second ? Resolution.Tick : castResolution, startDate, endDate));
                    var bars = data.Cast<TradeBar>().ToList();
                    
                    // Write data
                    var writer = new LeanDataWriter(castResolution, pairObject, dataDirectory);

                    IEnumerable<BaseData> aggregatedBars = null;
                    if (castResolution == Resolution.Second)
                    {
                        aggregatedBars = LeanData.AggregateTradeBars(bars, pairObject, castResolution.ToTimeSpan());
                    }
                    writer.Write(aggregatedBars ?? bars);
                }
            }
            catch (Exception err)
            {
                Log.Error(err);
            }
        }
        
        /// <summary>
        /// Endpoint for downloading exchange info
        /// </summary>
        public static void ExchangeInfoDownloader()
        {
            new ExchangeInfoUpdater(new KrakenExchangeInfoDownloader())
                .Run();
        }
    }
}
