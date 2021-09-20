using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Brokerages.Kraken.Models;

namespace QuantConnect.Brokerages.Kraken.Converters
{
    /// <summary>
    /// A custom JSON converter for the Kraken <see cref="KrakenSpread"/> class
    /// </summary>
    public class KrakenSpreadConverter : JsonConverter
    {
        /// <summary>
        /// Gets a value indicating whether this <see cref="T:Newtonsoft.Json.JsonConverter" /> can write JSON.
        /// </summary>
        /// <value><c>true</c> if this <see cref="T:Newtonsoft.Json.JsonConverter" /> can write JSON; otherwise, <c>false</c>.</value>
        public override bool CanWrite => false;

        /// <summary>Writes the JSON representation of the object.</summary>
        /// <param name="writer">The <see cref="T:Newtonsoft.Json.JsonWriter" /> to write to.</param>
        /// <param name="value">The value.</param>
        /// <param name="serializer">The calling serializer.</param>
        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }

        /// <summary>Reads the JSON representation of the object.</summary>
        /// <param name="reader">The <see cref="T:Newtonsoft.Json.JsonReader" /> to read from.</param>
        /// <param name="objectType">Type of the object.</param>
        /// <param name="existingValue">The existing value of object being read.</param>
        /// <param name="serializer">The calling serializer.</param>
        /// <returns>The object value.</returns>
        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            var array = JArray.Load(reader);

            return new KrakenSpread
            {
                Bid = array[0].Type == JTokenType.Null ? 0 : Convert.ToDecimal((string) array[0]),
                Ask = array[1].Type == JTokenType.Null ? 0 : Convert.ToDecimal((string) array[1]),
                Timestamp = array[2].Type == JTokenType.Null ? 0 : Convert.ToDouble((string) array[2]),
                BidVolume = array[3].Type == JTokenType.Null ? 0 : Convert.ToDecimal((string) array[3]),
                AskVolume = array[4].Type == JTokenType.Null ? 0 : Convert.ToDecimal((string) array[4]),
            };
        }

        /// <summary>
        /// Determines whether this instance can convert the specified object type.
        /// </summary>
        /// <param name="objectType">Type of the object.</param>
        /// <returns>
        /// 	<c>true</c> if this instance can convert the specified object type; otherwise, <c>false</c>.
        /// </returns>
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(KrakenSpread);
        }
    }
}