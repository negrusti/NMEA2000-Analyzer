﻿using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NMEA2000Analyzer
{
    public static class PgnDefinitions
    {
        private const string CanboatJsonUrl = "https://raw.githubusercontent.com/canboat/canboat/master/docs/canboat.json";
        private static readonly string CanboatJsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "canboat.json");
        private static readonly string LocalJsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "local.json");
        private static readonly string CustomJsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "custom.json");
        public static async Task<Dictionary<string, CanboatPgn>> LoadPgnDefinitionsAsync()
        {
            if (!File.Exists(CanboatJsonPath))
            {
                await DownloadCanboatJsonAsync();
            }

            try
            {
                var CanboatJSON = File.ReadAllText(CanboatJsonPath);
                var LocalJSON = File.ReadAllText(LocalJsonPath);

                // Parse JSON strings into JObject
                var canboatJObject = JObject.Parse(CanboatJSON);
                var localJObject = JObject.Parse(LocalJSON);

                // Merge localJObject into canboatJObject
                canboatJObject.Merge(localJObject, new JsonMergeSettings
                {
                    MergeArrayHandling = MergeArrayHandling.Concat, // Options: Replace, Union, Concat, etc.
                    MergeNullValueHandling = MergeNullValueHandling.Ignore // Ignore null values from localJson
                });

                // Serialize back to JSON string
                var mergedJSON = canboatJObject.ToString(Formatting.Indented);

                // Deserialize with PGNs as a list
                var canboatData = System.Text.Json.JsonSerializer.Deserialize<CanboatRoot>(mergedJSON, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (canboatData?.PGNs == null || !canboatData.PGNs.Any())
                {
                    throw new Exception("The JSON file does not contain valid PGN definitions.");
                }

                // Convert List<CanboatPgn> to Dictionary<string, CanboatPgn>, ignoring duplicates
                var pgnDictionary = canboatData.PGNs
                    .GroupBy(pgn => pgn.PGN.ToString()) // Group by PGN as a string
                    .ToDictionary(
                        group => group.Key,             // Use the PGN as the key
                        group => group.First()          // Take the first entry for each PGN
                    );

                return pgnDictionary;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to load PGN definitions: {ex.Message}", ex);
            }
        }

        public static async Task<Canboat.Rootobject> LoadPgnDefinitionsNewAsync()
        {
            if (!File.Exists(CanboatJsonPath))
            {
                await DownloadCanboatJsonAsync();
            }

            try
            {
                var jsonContent = File.ReadAllText(CanboatJsonPath);

                var canboatData = System.Text.Json.JsonSerializer.Deserialize<Canboat.Rootobject>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (canboatData?.PGNs == null || !canboatData.PGNs.Any())
                {
                    throw new Exception("The JSON file does not contain valid PGN definitions.");
                }

                return canboatData;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to load PGN definitions: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Downloads the canboat.json file from the default URL and saves it locally.
        /// </summary>
        private static async Task DownloadCanboatJsonAsync()
        {
            try
            {
                using var httpClient = new HttpClient();
                var jsonData = await httpClient.GetStringAsync(CanboatJsonUrl);

                // Save the file locally
                File.WriteAllText(CanboatJsonPath, jsonData);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to download canboat.json from {CanboatJsonUrl}: {ex.Message}", ex);
            }
        }

        public class CanboatRoot
        {
            public List<CanboatPgn> PGNs { get; set; }
        }

        public class CanboatPgn
        {
            public int PGN { get; set; }
            public string Id { get; set; }
            public string Description { get; set; }
            public int Priority { get; set; }
            public string Explanation { get; set; }
            public string Type { get; set; }
            public bool Complete { get; set; }
            public int FieldCount { get; set; }
            public int Length { get; set; }
            public bool TransmissionIrregular { get; set; }
            public List<CanboatField> Fields { get; set; }
        }



        public class CanboatField
        {
            public int Order { get; set; }
            public string Id { get; set; }
            public string Name { get; set; }
            public int BitLength { get; set; }
            public int BitOffset { get; set; }
            public int BitStart { get; set; }
            public double Resolution { get; set; }
            public bool Signed { get; set; }
            public double? RangeMin { get; set; }
            public double? RangeMax { get; set; }
            public string FieldType { get; set; }
            public string LookupEnumeration { get; set; }
            public string Description { get; set; }
        }

        public static JsonObject DecodePgnData(byte[] pgnData, Canboat.Pgn pgnDefinition)
        {
            if (pgnDefinition == null)
                throw new ArgumentNullException(nameof(pgnDefinition));

            if (pgnData == null || pgnData.Length * 8 < pgnDefinition.Length)
                throw new ArgumentException("PGN data is null or insufficient for decoding.");

            var jsonObject = new JsonObject
            {
                ["PGN"] = pgnDefinition.PGN,
                ["Description"] = pgnDefinition.Description,
                ["Type"] = pgnDefinition.Type,
                ["Fields"] = new JsonArray()
            };

            foreach (var field in pgnDefinition.Fields)
            {
                if (field.FieldType == "RESERVED")
                    continue; // Skip reserved fields

                int byteStart = field.BitOffset / 8;
                int bitStart = field.BitOffset % 8;
                int bitLength = field.BitLength;

                // Extract the bits from the raw PGN data
                ulong rawValue = ExtractBits(pgnData, byteStart, bitStart, bitLength);

                // Decode the value
                object decodedValue = DecodeFieldValue(rawValue, field);

                // Add to JSON output
                var fieldJson = new JsonObject
                {
                    ["Name"] = field.Name,
                    ["Value"] = JsonValue.Create(decodedValue) // Explicitly create a JsonValue
                };

                if (!string.IsNullOrEmpty(field.Unit))
                    fieldJson["Unit"] = field.Unit;

                if (!string.IsNullOrEmpty(field.Unit))
                    fieldJson["Unit"] = field.Unit;

                jsonObject["Fields"].AsArray().Add(fieldJson);
            }

            return jsonObject;
        }

        // Function to extract bits from the data
        private static ulong ExtractBits(byte[] data, int byteStart, int bitStart, int bitLength)
        {
            ulong value = 0;

            for (int i = 0; i < bitLength; i++)
            {
                int byteIndex = byteStart + (bitStart + i) / 8;
                int bitIndex = (bitStart + i) % 8;

                if (byteIndex >= data.Length)
                    throw new ArgumentOutOfRangeException("Bit extraction exceeds data length.");

                if ((data[byteIndex] & (1 << bitIndex)) != 0)
                    value |= (1UL << i);
            }

            return value;
        }

        // Function to decode the field value based on the field properties
        private static object DecodeFieldValue(ulong rawValue, Canboat.Field field)
        {
            if (field.FieldType == "LOOKUP" && !string.IsNullOrEmpty(field.LookupEnumeration))
            {
                // Find the matching LookupEnumeration
                var lookupEnum = ((App)Application.Current).CanboatRootNew.LookupEnumerations
                    .FirstOrDefault(le => le.Name == field.LookupEnumeration);

                if (lookupEnum != null)
                {
                    // Find the matching value in the EnumValues
                    var lookupValue = lookupEnum.EnumValues.FirstOrDefault(ev => ev.Value == (int)rawValue)?.Name;

                    if (lookupValue != null)
                    {
                        return lookupValue; // Return the string representation
                    }

                    return $"Unknown ({rawValue})"; // No match found
                }

                return $"Unknown Enumeration ({field.LookupEnumeration})"; // LookupEnumeration not found
            }

            // Decode numeric value
            double decodedValue = rawValue * field.Resolution;

            // Handle signed values
            if (field.Signed && field.BitLength > 1 && (rawValue & (1UL << (field.BitLength - 1))) != 0)
            {
                long signedValue = (long)(rawValue | ~((1UL << field.BitLength) - 1));
                decodedValue = signedValue * field.Resolution;
            }

            return decodedValue;
        }
    }
}
