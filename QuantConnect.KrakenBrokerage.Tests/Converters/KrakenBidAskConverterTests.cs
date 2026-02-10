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

using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using QuantConnect.Brokerages.Kraken.Converters;
using QuantConnect.Brokerages.Kraken.Models;

namespace QuantConnect.Tests.Brokerages.Kraken.Converters;

public class KrakenBidAskConverterTests
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        Converters = [new DecimalConverter()]
    };

    [TestCase(new object[] { "68510.90000", "0.61335740", "1770727721.057800" },68510.90, 0.61335740, 1770727721.057800)]
    public void JsonConverterTests(object[] data, decimal price, decimal volume, decimal time)
    {
        var stringifiedData = data.Select(o =>
            o switch
            {
                string s => "\"" + s + "\"",
                _ => o.ToString()
            }
        );
        var arrayString = '[' + string.Join(',', stringifiedData) + ']';
        var candle = JsonConvert.DeserializeObject<KrakenBidAsk>(arrayString, Settings);

        Assert.AreEqual(time, candle.Timestamp);
        Assert.AreEqual(price, candle.Price);
        Assert.AreEqual(volume, candle.Volume);
    }
}