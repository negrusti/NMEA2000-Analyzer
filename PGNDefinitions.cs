using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using static NMEA2000Analyzer.MainWindow;

namespace NMEA2000Analyzer
{
    public static class PgnDefinitions
    {
        private const string CanboatJsonUrl = "https://raw.githubusercontent.com/canboat/canboat/master/docs/canboat.json";
        private static readonly string CanboatJsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "canboat.json");
        private static readonly string LocalJsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "local.json");

        public static async Task<Canboat.Rootobject> LoadPgnDefinitionsAsync()
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

                var canboatData = System.Text.Json.JsonSerializer.Deserialize<Canboat.Rootobject>(mergedJSON, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (canboatData?.PGNs == null || !canboatData.PGNs.Any())
                {
                    throw new Exception("The JSON file does not contain valid PGN definitions.");
                }

                Globals.PGNsWithMfgCode = GetPgnsWithManufacturerCode(canboatData.PGNs);
                Globals.UniquePGNs = canboatData.PGNs
                    .GroupBy(item => item.PGN)  // Group by the PGN property
                    .ToDictionary(group => group.Key, group => group.First());

                ComputeMatchValueAndBitmask(canboatData.PGNs);


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

        public static JsonObject? DecodePgnData(byte[] pgnData, Canboat.Pgn pgnDefinition)
        {
            if (pgnDefinition == null)
                return null;

            if (pgnData == null || pgnData.Length * 8 < pgnDefinition.Length)
                throw new ArgumentException("PGN data is null or insufficient for decoding.");

            var jsonObject = new JsonObject
            {
                ["PGN"] = pgnDefinition.PGN,
                ["Description"] = pgnDefinition.Description,
                ["Type"] = pgnDefinition.Type,
                ["Fields"] = new JsonArray()
            };

            if (pgnDefinition.Fields == null || pgnDefinition.Fields.Length == 0)
                return jsonObject;

            // Determine repeated field set range
            int repeatedFieldStart = pgnDefinition.RepeatingFieldSet1StartField - 1;
            int repeatedFieldCount = pgnDefinition.RepeatingFieldSet1Size;

            for (int i = 0; i < pgnDefinition.Fields.Length; i++)
            {
                var field = pgnDefinition.Fields[i];
                object? finalValue = null;

                if (field.FieldType == "RESERVED")
                    continue; // Skip reserved fields
                
                // Skip fields that belong to the repeated set, as they will be processed later
                if (repeatedFieldStart >= 0 && i >= repeatedFieldStart && i < repeatedFieldStart + repeatedFieldCount)
                    continue;

                if (field.FieldType == "STRING_FIX")
                {
                    var rawBytes = new byte[field.BitLength / 8];
                    Array.Copy(pgnData, field.BitOffset / 8, rawBytes, 0, field.BitLength / 8);
                    var filteredBytes = rawBytes.Where(b => b != 0xFF && b != 0x00).ToArray();

                    // Convert the bytes to an ASCII string and trim padding characters
                    finalValue = Encoding.ASCII.GetString(filteredBytes).TrimEnd('@', ' ');
                }
                else if (field.FieldType == "INDIRECT_LOOKUP")
                {

                    int byteStart = field.BitOffset / 8;
                    int bitStart = field.BitOffset % 8;
                    int bitLength = field.BitLength;

                    int rawValue = (int)ExtractBits(pgnData, byteStart, bitStart, bitLength);

                    var indirectField = pgnDefinition.Fields[field.LookupIndirectEnumerationFieldOrder - 1];

                    byteStart = indirectField.BitOffset / 8;
                    bitStart = indirectField.BitOffset % 8;
                    bitLength = indirectField.BitLength;

                    int rawValueIndirectField = (int)ExtractBits(pgnData, byteStart, bitStart, bitLength);
                    finalValue = LookupIndirect(field.LookupIndirectEnumeration, rawValueIndirectField, rawValue);
                }
                else
                {
                    int byteStart = field.BitOffset / 8;
                    int bitStart = field.BitOffset % 8;
                    int bitLength = field.BitLength;

                    // Extract the bits from the raw PGN data
                    ulong rawValue = ExtractBits(pgnData, byteStart, bitStart, bitLength);

                    // Decode the value
                    object decodedValue = DecodeFieldValue(rawValue, field);

                    if (decodedValue is double rangedValue &&
                        ((field.RangeMin != null && rangedValue < field.RangeMin) ||
                         (field.RangeMax != null && rangedValue > field.RangeMax)))
                        continue;

                    // Convert units if applicable
                    finalValue = ApplyUnitConversion(decodedValue, field);

                }

                // Add to JSON output
                var fieldJson = new JsonObject();
                fieldJson[field.Name] = JsonValue.Create(finalValue);
                jsonObject["Fields"]?.AsArray().Add(fieldJson);
            }

            // Handle repeated field sets
            if (repeatedFieldCount > 0 && repeatedFieldStart >= 0)
            {
                var repeatedFieldsArray = new JsonArray();

                // Determine the number of repetitions from the count field
                var countField = pgnDefinition.Fields[pgnDefinition.RepeatingFieldSet1CountField - 1];
                int byteStart = countField.BitOffset / 8;
                int bitStart = countField.BitOffset % 8;
                int bitLength = countField.BitLength;

                ulong repetitionCount = ExtractBits(pgnData, byteStart, bitStart, bitLength);

                for (int i = 0; i < (int)repetitionCount; i++)
                {
                    var repeatedFieldSet = new JsonObject();

                    for (int j = 0; j < repeatedFieldCount; j++)
                    {
                        var field = pgnDefinition.Fields[repeatedFieldStart + j];

                        int repeatBitOffset = field.BitOffset + (i * field.BitLength);
                        int repeatByteStart = repeatBitOffset / 8;
                        int repeatBitStart = repeatBitOffset % 8;
                        int repeatBitLength = field.BitLength;

                        ulong rawValue = ExtractBits(pgnData, repeatByteStart, repeatBitStart, repeatBitLength);
                        object decodedValue = DecodeFieldValue(rawValue, field);

                        if (decodedValue is double rangedValue &&
                            ((field.RangeMin != null && rangedValue < field.RangeMin) ||
                             (field.RangeMax != null && rangedValue > field.RangeMax)))
                            continue;

                        // Convert units if applicable
                        object finalValue = ApplyUnitConversion(decodedValue, field);

                        repeatedFieldSet[field.Name] = JsonValue.Create(finalValue);
                    }

                    if (repeatedFieldSet.Count > 0)
                        repeatedFieldsArray.Add(repeatedFieldSet);
                }

                jsonObject["RepeatedFields"] = repeatedFieldsArray;
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
                    //throw new ArgumentOutOfRangeException($"Bit extraction exceeds data length: ByteIdx {byteIndex} Data Len {data.Length}");
                    
                    // DEBUG Remove and restore the exception
                    return 0;

                if ((data[byteIndex] & (1 << bitIndex)) != 0)
                    value |= (1UL << i);
            }

            return value;
        }

        // Function to decode the field value based on the field properties
        private static object? DecodeFieldValue(ulong rawValue, Canboat.Field field)
        {
            switch (field.FieldType)
            {
                case "NUMBER":
                case "MMSI":
                case "TIME":
                case "DATE":
                    double decodedNumberValue = rawValue * field.Resolution;

                    // Handle signed values
                    if (field.Signed && field.BitLength > 1 && (rawValue & (1UL << (field.BitLength - 1))) != 0)
                    {
                        long signedValue = (long)(rawValue | ~((1UL << field.BitLength) - 1));
                        decodedNumberValue = signedValue * field.Resolution;
                    }

                    return decodedNumberValue;

                case "FLOAT":
                    double decodedFloatValue = rawValue * field.Resolution;

                    // Handle signed values
                    if (field.Signed && field.BitLength > 1 && (rawValue & (1UL << (field.BitLength - 1))) != 0)
                    {
                        long signedValue = (long)(rawValue | ~((1UL << field.BitLength) - 1));
                        decodedFloatValue = signedValue * field.Resolution;
                    }

                    return decodedFloatValue;

                case "DECIMAL":
                    double decodedDecimalValue = rawValue * field.Resolution;

                    // Handle signed values
                    if (field.Signed && field.BitLength > 1 && (rawValue & (1UL << (field.BitLength - 1))) != 0)
                    {
                        long signedValue = (long)(rawValue | ~((1UL << field.BitLength) - 1));
                        decodedDecimalValue = signedValue * field.Resolution;
                    }

                    return decodedDecimalValue;

                case "LOOKUP":
                    return Lookup(field.LookupEnumeration, (int)rawValue);

                case "INDIRECT_LOOKUP":
                    break;
                case "BITLOOKUP":
                    break;
                case "FIELDTYPE_LOOKUP":
                    break;
                case "STRING_LZ":
                    break;
                case "STRING_LAU":
                    break;
                case "BINARY":
                    break;
                case "RESERVED":
                    break;
                case "SPARE":
                    break;
                case "VARIABLE":
                    break;
                case "KEY_VALUE":
                    break;
                case "FIELD_INDEX":
                    break;
                default:
                    throw new NotSupportedException($"Field type '{field.FieldType}' is not supported.");
            }
            return null;

        }

        public static string Lookup(string enumeration, int value)
        {
            // Find the matching LookupEnumeration
            var lookupEnum = ((App)Application.Current).CanboatRoot.LookupEnumerations
                .FirstOrDefault(le => le.Name == enumeration);

            if (lookupEnum != null)
            {
                // Find the matching value in the EnumValues
                var lookupValue = lookupEnum.EnumValues.FirstOrDefault(ev => ev.Value == value)?.Name;

                if (lookupValue != null)
                {
                    return lookupValue; // Return the string representation
                }

                return $"Unknown ({value})"; // No match found
            }

            return $"Unknown Enumeration ({enumeration})"; // LookupEnumeration not found

        }
        public static string LookupIndirect(string enumeration, int value1, int value2)
        {
            var lookupEnum = ((App)Application.Current).CanboatRoot.LookupIndirectEnumerations
                .FirstOrDefault(le => le.Name == enumeration);

            if (lookupEnum != null)
            {

                var match = lookupEnum.EnumValues
                .FirstOrDefault(ev => ev.Value1 == value1 && ev.Value2 == value2);

                if (match != null)
                {
                    return match.Name;
                }

                // Return a default message if no match is found
                return $"Unknown ({value1} {value2})";
            }
            
            return $"Unknown Enumeration ({enumeration})"; // LookupEnumeration not found
        }

        private static object ApplyUnitConversion(object decodedValue, Canboat.Field field)
        {
            if (decodedValue is double numericValue)
            {
                if (field.Unit == "m/s")
                {
                    // Convert to knots
                    var valueInKnots = numericValue * 1.94384; // Conversion factor
                    return $"{valueInKnots:F2} kts"; // Format to 2 decimal places
                }
                else if (field.Unit == "rad")
                {
                    // Convert to degrees
                    var valueInDegrees = numericValue * (180.0 / Math.PI);
                    return $"{valueInDegrees:F1} deg"; // Format to 1 decimal place
                }
                else if (field.Unit == "rad/s")
                {
                    // Convert to degrees
                    var valueInDegrees = numericValue * (180.0 / Math.PI);
                    return $"{valueInDegrees:F1} deg/s"; // Format to 1 decimal place
                }
                else if (field.Unit == "K")
                {
                    // Convert to Celsius
                    var valueInCelsius = numericValue - 273.15;
                    return $"{valueInCelsius:F1} deg C"; // Format to 1 decimal place
                }
                else if (field.Unit == "s")
                {
                    // Convert seconds to H:M:S format
                    TimeSpan time = TimeSpan.FromSeconds(numericValue);
                    string hms = $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
                    return $"{hms}";
                }
                else if (field.Unit == "d")
                {
                    return $"{DateTime.UnixEpoch.AddDays(numericValue):yyyy-MM-dd}";
                }
                else if (field.Unit == "deg")
                {
                    return $"{(int)numericValue} deg {Math.Abs((numericValue - (int)numericValue) * 60):F4} min";
                }

                else if (!string.IsNullOrEmpty(field.Unit))
                {
                    // If a unit exists but no special conversion is needed
                    return $"{numericValue:F2} {field.Unit}";
                }
            }

            // If the value doesn't require conversion, return it as-is
            return decodedValue;
        }

        public static Canboat.Pgn MatchPgnFromRecord(
            List<Canboat.Pgn> pgnList,
            Nmea2000Record record)
        {
            // Filter the list to only include PGNs that match the given PGN number
            var matchingPgns = pgnList.Where(p => p.PGN == Convert.ToUInt32(record.PGN));

            // Convert Data string (hex values separated by spaces) to a byte array
            var dataBytes = record.Data
                .Split(' ')
                .Select(hex => Convert.ToByte(hex, 16))
                .ToArray();

            foreach (var pgnDefinition in matchingPgns)
            {
                bool isMatch = true;

                foreach (var field in pgnDefinition.Fields)
                {
                    if (field.Match.HasValue)
                    {
                        // Extract the field value
                        ulong rawFieldValue = ExtractFieldValue(dataBytes, field);

                        // Decode the field value using DecodeFieldValue
                        var decodedValue = DecodeFieldValue(rawFieldValue, field);

                        // Check if the decoded value matches the expected Match value
                        if (!decodedValue.Equals(field.Match.Value))
                        {
                            isMatch = false;
                            break; // Exit early if a mismatch is found
                        }
                    }
                }

                if (isMatch)
                {
                    return pgnDefinition;
                }
            }

            return null; // No matching PGN found
        }
        private static ulong ExtractFieldValue(byte[] dataBytes, Canboat.Field field)
        {
            // Calculate the starting byte and bit positions
            int byteIndex = field.BitOffset / 8;
            int bitOffsetInByte = field.BitOffset % 8;

            // Calculate the number of bytes the field spans
            int bitLength = field.BitLength;
            int byteSpan = (bitLength + bitOffsetInByte + 7) / 8; // Round up to the next full byte

            // Extract the relevant bytes
            ulong extractedValue = 0;
            for (int i = 0; i < byteSpan; i++)
            {
                if (byteIndex + i < dataBytes.Length)
                {
                    extractedValue |= (ulong)dataBytes[byteIndex + i] << (8 * i); // Combine bytes into a single value
                }
            }

            // Shift to remove extra leading bits and mask the field length
            extractedValue >>= bitOffsetInByte; // Adjust for the bit offset within the starting byte
            extractedValue &= (ulong)((1L << bitLength) - 1); // Mask to keep only the number of bits defined by BitLength

            return extractedValue;
        }

        public static Dictionary<int, int> GetPgnsWithManufacturerCode(List<Canboat.Pgn> pgnList)
        {
            return pgnList
               .GroupBy(pgn => pgn.PGN)
               .ToDictionary(group => group.Key, group => group.Count());
        }
        public static (ulong Mask, ulong MatchValue) GetBitmapMaskAndMatchValue(Canboat.Pgn pgn)
        {
            ulong mask = 0;
            ulong matchValue = 0;

            foreach (var field in pgn.Fields)
            {
                if (field.Match.HasValue) // Check if the field has a Match property
                {
                    // Create the bitmask for the field
                    ulong fieldMask = ((1UL << field.BitLength) - 1) << field.BitOffset;
                    mask |= fieldMask;

                    // Combine the match value into the proper bit offset
                    matchValue |= ((ulong)field.Match.Value << field.BitOffset);
                }
            }

            return (mask, matchValue);
        }

        public static void GenerateDeviceInfo(List<Nmea2000Record> records)
        {
            // PGN 126996: Product Information
            var filteredRecords = records.Where(record => record.PGN == "126996").ToList();
            foreach (var record in filteredRecords)
            {
                var dataBytes = record.Data.Split(' ').Select(b => Convert.ToByte(b, 16)).ToArray();
                var JSONObject = DecodePgnData(dataBytes, ((App)Application.Current).CanboatRoot.PGNs.FirstOrDefault(q => q.PGN.ToString() == record.PGN));

                var fields = ((JsonArray)JSONObject["Fields"]!).OfType<JsonObject>();
                Device dev = new Device
                {
                    Address = Convert.ToByte(record.Source),
                    ProductCode = fields.FirstOrDefault(obj => obj.ContainsKey("Product Code"))?["Product Code"]?.GetValue<double>(),
                    ModelID = fields.FirstOrDefault(obj => obj.ContainsKey("Model ID"))?["Model ID"]?.GetValue<string>(),
                    SoftwareVersionCode = fields.FirstOrDefault(obj => obj.ContainsKey("Software Version Code"))?["Software Version Code"]?.GetValue<string>(),
                    ModelVersion = fields.FirstOrDefault(obj => obj.ContainsKey("Model Version"))?["Model Version"]?.GetValue<string>(),
                    ModelSerialCode = fields.FirstOrDefault(obj => obj.ContainsKey("Model Serial Code"))?["Model Serial Code"]?.GetValue<string>(),
                };

                Globals.Devices.TryAdd(Convert.ToByte(record.Source), dev);
            }

            /*
            // PGN 60928: ISO Address Claim
            filteredRecords = records.Where(record => record.PGN == "60928").ToList();
            foreach (var record in filteredRecords)
            {
                var dataBytes = record.Data.Split(' ').Select(b => Convert.ToByte(b, 16)).ToArray();
                var JSONObject = DecodePgnData(dataBytes, ((App)Application.Current).CanboatRoot.PGNs.FirstOrDefault(q => q.PGN.ToString() == record.PGN));

                var fields = ((JsonArray)JSONObject["Fields"]!).OfType<JsonObject>();

                Device dev = new Device
                {
                    Address = Convert.ToByte(record.Source),
                    MfgCode = fields.FirstOrDefault(obj => obj.ContainsKey("Manufacturer Code"))?["Manufacturer Code"]?.GetValue<string>(),
                    DeviceClass = fields.FirstOrDefault(obj => obj.ContainsKey("Device Class"))?["Device Class"]?.GetValue<string>(),
                    DeviceFunction = fields.FirstOrDefault(obj => obj.ContainsKey("Device Function"))?["Device Function"]?.GetValue<string>()
                };
                
                Globals.Devices.TryAdd(Convert.ToByte(record.Source), dev);
            }
            */
        }
        public static void ComputeMatchValueAndBitmask(List<Canboat.Pgn> pgnDefinitions)
        {
            var tupleList = new List<(int PGN, ulong MatchValue, ulong Mask, int PGNIndex)>();

            for (int i = 0; i < pgnDefinitions.Count; i++)
            {
                var pgnDefinition = pgnDefinitions[i];
                ulong matchValue = 0;
                ulong mask = 0;
                bool condidionalPgn = false;

                if (pgnDefinition.Fields == null || pgnDefinition.Fields.Length == 0)
                    continue;

                foreach (var field in pgnDefinition.Fields)
                {
                    if (field.Match.HasValue)
                    {
                        // Create a mask for this field
                        ulong fieldMask = ((1UL << field.BitLength) - 1) << field.BitOffset;

                        // Add the field's Match value to the MatchValue
                        matchValue |= ((ulong)field.Match.Value << field.BitOffset);

                        // Add the field's mask to the overall mask
                        mask |= fieldMask;
                        condidionalPgn = true;
                    }
                }

                if (condidionalPgn)
                {
                    // Add to the dictionary
                    tupleList.Add((Convert.ToInt32(pgnDefinitions[i].PGN), matchValue, mask, i));
                    // Debug.WriteLine($"Matching value: {matchValue.ToString("X16")} Mask: {mask.ToString("X16")} PGN: {pgnDefinitions[i].PGN} {pgnDefinitions[i].Description}");
                }
                Globals.InitializePGNLookup(tupleList);
            }
        }

        public static int? MatchDataAgainstLookup(byte[] dataBytes, int pgn)
        {
            if (dataBytes.Length < 8)
            {
                var paddedBytes = new byte[8];
                Array.Copy(dataBytes, paddedBytes, dataBytes.Length);
                dataBytes = paddedBytes;
            }

            // Convert the first 8 bytes to ulong
            ulong data = BitConverter.ToUInt64(dataBytes, 0);

            // Iterate through the dictionary
            foreach (var matcher in Globals.PGNListLookup[pgn])
            {
                ulong matchValue = matcher.Item1;
                ulong mask = matcher.Item2;

                // Apply the mask and check against the match value
                if ((data & mask) == matchValue)
                {
                    return matcher.Item3; // Return the matching PGN Index
                }
            }

            return null; // No match found
        }
    }
}