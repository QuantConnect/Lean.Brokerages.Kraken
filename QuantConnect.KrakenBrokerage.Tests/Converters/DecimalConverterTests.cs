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

using System.Collections.Generic;
using Newtonsoft.Json;
using NUnit.Framework;
using QuantConnect.Brokerages.Kraken.Converters;

namespace QuantConnect.Tests.Brokerages.Kraken.Converters;

public class DecimalConverterTests
{
    [TestCase("1", 1, true)]
    [TestCase("0", 0, true)]
    [TestCase("-1", -1, true)]
    [TestCase("1.333", 1.333, true)]
    [TestCase("1.333", 1.333, false)]
    [TestCase("9e-8", 0.00000009, true)]
    [TestCase("1.3117285e+06", 1311728.5, true)]
    [TestCase("9e-8", 0.00000009, false)]
    [TestCase("1.3117285e+06", 1311728.5, false)]
    public void BybitDecimalStringConverterTests(string value, decimal expected, bool quote = true)
    {
        var settings = new JsonSerializerSettings
        {
            Converters = new List<JsonConverter>
            {
                new DecimalConverter()
            }
        };

        var jsonString = TestObject<decimal>.CreateJsonObject(value, quote);
        var obj = JsonConvert.DeserializeObject<TestObject<decimal>>(jsonString, settings);

        Assert.AreEqual(expected, obj.Value);
    }

    private class TestObject<T>
    {
        public T Value { get; set; }

        public static string CreateJsonObject(string value, bool quote = false)
        {
            if (quote)
            {
                value = $"\"{value}\"";
            }

            return $$"""
                         {"value": {{value}}}
                     """;
        }
    }
}