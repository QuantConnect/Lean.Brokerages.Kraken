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
using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace QuantConnect.ToolBox.KrakenDownloader
{
    public class KrakenExchangeInfoDownloader : IExchangeInfoDownloader
    {
        public string Market => QuantConnect.Market.Kraken;
        public IEnumerable<string> Get()
        {
            const string url = "https://api.kraken.com/0/public/AssetPairs";
            var json = url.DownloadData();
           
            var t = JToken.Parse(json);
            foreach (JProperty instr in t["result"].Children())
            {
                if(instr.Name.EndsWith(".d")) continue;
                if (instr.Value["altname"].ToString().StartsWith("XBT") || instr.Value["altname"].ToString().EndsWith("XBT"))
                {
                    instr.Value["altname"] = instr.Value["altname"].ToString().Replace("XBT", "BTC");
                }
                var priceDecimals = Convert.ToDecimal(Math.Round(Math.Pow(0.1, Convert.ToInt32(instr.Value["pair_decimals"])), Convert.ToInt32(instr.Value["pair_decimals"])));
                var quantityDecimals = Convert.ToDecimal(Math.Round(Math.Pow(0.1, Convert.ToInt32(instr.Value["lot_decimals"])), Convert.ToInt32(instr.Value["lot_decimals"])));
                var quote = instr.Value["wsname"].ToString().Split("/")[1];
            
                if (quote == "XBT")
                {
                    quote = "BTC";
                }
                yield return $"kraken,{instr.Value["altname"]},crypto,{instr.Value["wsname"]},{quote},{instr.Value["lot_multiplier"]},{priceDecimals},{quantityDecimals},{instr.Name},{instr.Value["ordermin"]}";
            }
        }
    }
}
