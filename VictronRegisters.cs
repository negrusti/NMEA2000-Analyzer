using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NMEA2000Analyzer
{
    internal static class VictronRegisters
    {
        private static readonly string RegisterJsonPath = Path.Combine(AppContext.BaseDirectory, "victron.json");
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
        private static Dictionary<ushort, VictronRegisterDefinition>? _definitionsById;

        public static JsonObject? Decode(ushort registerId, uint payload)
        {
            var definitions = GetDefinitionsById();
            if (!definitions.TryGetValue(registerId, out var definition))
            {
                return null;
            }

            var result = new JsonObject
            {
                ["Id"] = FormatRegisterId(registerId),
                ["Name"] = definition.Name,
                ["Access"] = definition.Access,
                ["Value"] = DecodeValue(definition, payload),
                ["Raw"] = $"0x{payload:X8}"
            };

            if (!string.IsNullOrWhiteSpace(definition.Status))
            {
                result["Status"] = definition.Status;
            }

            return result;
        }

        private static Dictionary<ushort, VictronRegisterDefinition> GetDefinitionsById()
        {
            if (_definitionsById != null)
            {
                return _definitionsById;
            }

            if (!File.Exists(RegisterJsonPath))
            {
                _definitionsById = new Dictionary<ushort, VictronRegisterDefinition>();
                return _definitionsById;
            }

            var root = JsonSerializer.Deserialize<VictronRegisterRoot>(
                File.ReadAllText(RegisterJsonPath),
                JsonOptions);

            _definitionsById = (root?.Registers ?? Array.Empty<VictronRegisterDefinition>())
                .Where(definition => TryParseRegisterId(definition.Id, out _))
                .ToDictionary(
                    definition =>
                    {
                        TryParseRegisterId(definition.Id, out var parsedId);
                        return parsedId;
                    },
                    definition => definition);

            return _definitionsById;
        }

        private static JsonNode? DecodeValue(VictronRegisterDefinition definition, uint payload)
        {
            var rawValue = MaskPayload(payload, definition.DataType);
            if (TryParseUnavailable(definition.Unavailable, out var unavailable) && rawValue == unavailable)
            {
                return "Not available";
            }

            var numericValue = DecodeNumericValue(rawValue, definition.DataType);
            if (definition.EnumValues != null &&
                definition.EnumValues.TryGetValue(Convert.ToInt64(numericValue, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), out var enumName))
            {
                return enumName;
            }

            if (string.Equals(definition.DataType, "bitmask32", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeBitmask(definition, rawValue);
            }

            if (definition.Scale.HasValue)
            {
                var scaled = Convert.ToDouble(numericValue, CultureInfo.InvariantCulture) * definition.Scale.Value;
                var formatted = scaled.ToString(GetNumberFormat(definition.Scale.Value), CultureInfo.InvariantCulture);
                return string.IsNullOrWhiteSpace(definition.Unit) ? formatted : $"{formatted} {definition.Unit}";
            }

            return JsonValue.Create(numericValue);
        }

        private static ulong MaskPayload(uint payload, string? dataType)
        {
            return dataType?.ToLowerInvariant() switch
            {
                "un8" => payload & 0xFFU,
                "un16" or "sn16" => payload & 0xFFFFU,
                _ => payload
            };
        }

        private static object DecodeNumericValue(ulong rawValue, string? dataType)
        {
            return dataType?.ToLowerInvariant() switch
            {
                "sn16" => unchecked((short)rawValue),
                "sn32" => unchecked((int)rawValue),
                _ => rawValue
            };
        }

        private static JsonNode DecodeBitmask(VictronRegisterDefinition definition, ulong rawValue)
        {
            var activeFlags = new JsonArray();
            foreach (var flag in definition.BitFlags ?? Array.Empty<VictronBitFlag>())
            {
                if ((rawValue & (1UL << flag.Bit)) != 0)
                {
                    activeFlags.Add(flag.Name);
                }
            }

            return new JsonObject
            {
                ["Mask"] = $"0x{rawValue:X8}",
                ["Active Flags"] = activeFlags
            };
        }

        private static bool TryParseRegisterId(string? value, out ushort registerId)
        {
            registerId = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ushort.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out registerId)
                : ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out registerId);
        }

        private static bool TryParseUnavailable(string? value, out ulong unavailable)
        {
            unavailable = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out unavailable)
                : ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out unavailable);
        }

        private static string FormatRegisterId(ushort registerId)
        {
            return $"0x{registerId:X4}";
        }

        private static string GetNumberFormat(double scale)
        {
            var decimals = 0;
            var value = Math.Abs(scale);
            while (value < 1 && decimals < 6)
            {
                value *= 10;
                decimals++;
            }

            return decimals == 0 ? "0" : "0." + new string('0', decimals);
        }

        private sealed class VictronRegisterRoot
        {
            public VictronRegisterDefinition[] Registers { get; set; } = Array.Empty<VictronRegisterDefinition>();
        }

        private sealed class VictronRegisterDefinition
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Access { get; set; } = string.Empty;
            public string DataType { get; set; } = "raw32";
            public double? Scale { get; set; }
            public string? Unit { get; set; }
            public string? Unavailable { get; set; }
            public string? Status { get; set; }
            public Dictionary<string, string>? EnumValues { get; set; }
            public VictronBitFlag[]? BitFlags { get; set; }
        }

        private sealed class VictronBitFlag
        {
            public int Bit { get; set; }
            public string Name { get; set; } = string.Empty;
        }
    }
}
