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

public class KrakenCandlesConverterTests
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        Converters = [new DecimalConverter()]
    };

    [TestCase(new object[] { 1770721680, "2012.71", "2012.83", "2010.07", "2010.07", "2012.71", "4.98229722", 7 }, 1770721680, 2012.71, 2012.83, 2010.07, 2010.07, 2012.71, 4.98229722, 7)]
    public void JsonConverterTests(object[] data, long openTime, decimal open, decimal high, decimal low,
        decimal close,
        decimal vwap, decimal volume, int count)
    {
        var stringifiedData = data.Select(o =>
            o switch
            {
                string s => "\"" + s + "\"",
                _ => o.ToString()
            }
        );
        var arrayString = '[' + string.Join(',', stringifiedData) + ']';
        var candle = JsonConvert.DeserializeObject<KrakenCandle>(arrayString, Settings);

        Assert.AreEqual(openTime, candle.Time);
        Assert.AreEqual(open, candle.Open);
        Assert.AreEqual(high, candle.High);
        Assert.AreEqual(low, candle.Low);
        Assert.AreEqual(close, candle.Close);
        Assert.AreEqual(volume, candle.Volume);
        Assert.AreEqual(vwap, candle.VWap);
        Assert.AreEqual(count, candle.Count);
    }
}