using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace NMEA2000Analyzer
{
    public static class PgnDefinitions
    {
        private const string DefaultJsonUrl = "https://raw.githubusercontent.com/canboat/canboat/master/docs/canboat.json";
        private static readonly string LocalJsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "canboat.json");

        public static async Task<Dictionary<string, CanboatPgn>> LoadPgnDefinitionsAsync()
        {
            if (!File.Exists(LocalJsonPath))
            {
                await DownloadCanboatJsonAsync();
            }

            try
            {
                var jsonContent = File.ReadAllText(LocalJsonPath);

                // Deserialize with PGNs as a list
                var canboatData = JsonSerializer.Deserialize<CanboatRoot>(jsonContent, new JsonSerializerOptions
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

        /// <summary>
        /// Downloads the canboat.json file from the default URL and saves it locally.
        /// </summary>
        private static async Task DownloadCanboatJsonAsync()
        {
            try
            {
                using var httpClient = new HttpClient();
                var jsonData = await httpClient.GetStringAsync(DefaultJsonUrl);

                // Save the file locally
                File.WriteAllText(LocalJsonPath, jsonData);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to download canboat.json from {DefaultJsonUrl}: {ex.Message}", ex);
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
    }
}
