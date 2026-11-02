using System.Text.Json;
using System.Text.Json.Serialization;

namespace NMEA2000Analyzer
{
    public sealed class JsonStringCoercionConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            using var document = JsonDocument.ParseValue(ref reader);
            var element = document.RootElement;

            return element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.ToString();
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }
}
