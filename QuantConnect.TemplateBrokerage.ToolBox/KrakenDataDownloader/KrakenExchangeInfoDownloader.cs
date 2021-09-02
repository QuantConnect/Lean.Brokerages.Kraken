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
            HttpClient client = new HttpClient();
            client.BaseAddress = new Uri("https://api.kraken.com");
            
            var req = new HttpRequestMessage(HttpMethod.Get, "/0/public/AssetPairs");
            var resp = client.SendAsync(req).Result;
            var t = JToken.Parse(resp.Content.ReadAsStringAsync().Result);
            foreach (JProperty instr in t["result"].Children())
            {
                if(instr.Name.EndsWith(".d")) continue;
                if (instr.Value["altname"].ToString().StartsWith("XBT") || instr.Value["altname"].ToString().EndsWith("XBT"))
                {
                    instr.Value["altname"] = instr.Value["altname"].ToString().Replace("XBT", "BTC");
                }
                var priceDecimals = Convert.ToDecimal(Math.Round(Math.Pow(0.1, Convert.ToInt32(instr.Value["pair_decimals"])), Convert.ToInt32(instr.Value["pair_decimals"])));
                var quantityDecimals = Convert.ToDecimal(Math.Round(Math.Pow(0.1, Convert.ToInt32(instr.Value["lot_decimals"])), Convert.ToInt32(instr.Value["lot_decimals"])));
                var @base = instr.Value["wsname"].ToString().Split("/")[1];
            
                if (@base == "XBT")
                {
                    @base = "BTC";
                }
                yield return $"kraken,{instr.Value["altname"]},crypto,{instr.Value["wsname"]},{@base},{instr.Value["lot_multiplier"]},{priceDecimals},{quantityDecimals},{instr.Name}";
            }
        }
    }
}
