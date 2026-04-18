using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
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
                var canboatJson = File.ReadAllText(CanboatJsonPath);

                // Parse JSON strings into JObject
                var canboatJObject = JObject.Parse(canboatJson);

                if (File.Exists(LocalJsonPath))
                {
                    var localJson = File.ReadAllText(LocalJsonPath);
                    var localJObject = JObject.Parse(localJson);

                    // Merge local definitions into the upstream data when present.
                    canboatJObject.Merge(localJObject, new JsonMergeSettings
                    {
                        MergeArrayHandling = MergeArrayHandling.Concat,
                        MergeNullValueHandling = MergeNullValueHandling.Ignore
                    });
                }

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

            if (pgnData == null || (pgnDefinition.Length.HasValue && pgnData.Length * 8 < pgnDefinition.Length.Value))
                throw new ArgumentException("PGN data is null or insufficient for decoding.");

            var jsonObject = new JsonObject
            {
                ["PGN"] = pgnDefinition.PGN,
                ["Description"] = pgnDefinition.Description,
                ["Type"] = pgnDefinition.Type,
                ["Fields"] = new JsonArray()
            };
            var warnings = new JsonArray();

            if (pgnDefinition.Fields == null || pgnDefinition.Fields.Length == 0)
                return jsonObject;

            // Determine repeated field set range
            int repeatedFieldStart = pgnDefinition.RepeatingFieldSet1StartField - 1;
            int repeatedFieldCount = pgnDefinition.RepeatingFieldSet1Size;

            var currentBitCursor = 0;

            for (int i = 0; i < pgnDefinition.Fields.Length; i++)
            {
                var field = pgnDefinition.Fields[i];
                object? finalValue = null;
                var effectiveBitOffset = GetEffectiveBitOffset(field, currentBitCursor);

                if (field.FieldType == "RESERVED")
                {
                    currentBitCursor = AdvanceBitCursor(currentBitCursor, effectiveBitOffset, field.BitLength);
                    continue; // Skip reserved fields
                }
                 
                // Skip fields that belong to the repeated set, as they will be processed later
                if (repeatedFieldStart >= 0 && i >= repeatedFieldStart && i < repeatedFieldStart + repeatedFieldCount)
                    continue;

                try
                {
                    if (field.FieldType == "STRING_FIX")
                    {
                        var rawBytes = new byte[field.BitLength / 8];
                        Array.Copy(pgnData, effectiveBitOffset / 8, rawBytes, 0, field.BitLength / 8);
                        var filteredBytes = rawBytes.Where(b => b != 0xFF && b != 0x00).ToArray();

                        // Convert the bytes to an ASCII string and trim padding characters
                        finalValue = Encoding.ASCII.GetString(filteredBytes).TrimEnd('@', ' ');
                        currentBitCursor = AdvanceBitCursor(currentBitCursor, effectiveBitOffset, field.BitLength);
                    }
                    else if (field.FieldType == "STRING_LZ" || field.FieldType == "STRING_LAU")
                    {
                        var decodedString = DecodeVariableStringField(
                            pgnData,
                            effectiveBitOffset,
                            field,
                            allowTruncatedAtEnd: i == pgnDefinition.Fields.Length - 1);
                        finalValue = decodedString.Value;
                        currentBitCursor = AdvanceBitCursor(currentBitCursor, effectiveBitOffset, decodedString.BitsConsumed);
                    }
                    else if (field.FieldType == "DYNAMIC_FIELD_KEY")
                    {
                        var rawValue = ExtractBits(pgnData, effectiveBitOffset / 8, effectiveBitOffset % 8, field.BitLength);
                        finalValue = DecodeDynamicFieldKey(rawValue, field);
                        currentBitCursor = AdvanceBitCursor(currentBitCursor, effectiveBitOffset, field.BitLength);
                    }
                    else if (field.FieldType == "DYNAMIC_FIELD_VALUE")
                    {
                        var decodedDynamicField = DecodeDynamicFieldValue(pgnData, effectiveBitOffset, field, pgnDefinition);
                        finalValue = decodedDynamicField.Value;
                        currentBitCursor = AdvanceBitCursor(currentBitCursor, effectiveBitOffset, decodedDynamicField.BitsConsumed);
                    }
                    else
                    {
                        int byteStart = effectiveBitOffset / 8;
                        int bitStart = effectiveBitOffset % 8;
                        int bitLength = field.BitLength;

                        // Extract the bits from the raw PGN data
                        ulong rawValue = ExtractBits(pgnData, byteStart, bitStart, bitLength);

                        // Decode the value
                        object? decodedValue = DecodeFieldValue(rawValue, field, pgnData, pgnDefinition);

                        if (decodedValue is double rangedValue &&
                            ((field.RangeMin != null && rangedValue < field.RangeMin) ||
                             (field.RangeMax != null && rangedValue > field.RangeMax)))
                            continue;

                        // Convert units if applicable
                        finalValue = decodedValue == null ? null : ApplyUnitConversion(decodedValue, field);
                        currentBitCursor = AdvanceBitCursor(currentBitCursor, effectiveBitOffset, field.BitLength);
                    }
                }
                catch (Exception ex)
                {
                    AddDecodeWarning(warnings, $"Field '{field.Name}' skipped: {ex.Message}");
                    finalValue = "<decode error>";
                }

                // Add to JSON output
                var fieldJson = new JsonObject();
                fieldJson[field.Name] = CreateJsonNode(finalValue);
                jsonObject["Fields"]?.AsArray().Add(fieldJson);
            }

            // Handle repeated field sets
            if (repeatedFieldCount > 0 && repeatedFieldStart >= 0)
            {
                var repeatedFields = pgnDefinition.Fields
                    .Skip(repeatedFieldStart)
                    .Take(repeatedFieldCount)
                    .ToArray();

                if (TryDecodeAlertValueRepeatedFieldSet(pgnData, pgnDefinition, out var alertValueRepeatedFieldsArray, out var alertValueWarning))
                {
                    if (alertValueRepeatedFieldsArray.Count > 0)
                    {
                        jsonObject["RepeatedFields"] = alertValueRepeatedFieldsArray;
                    }

                    if (!string.IsNullOrWhiteSpace(alertValueWarning))
                    {
                        AddDecodeWarning(warnings, alertValueWarning);
                    }
                }
                else if (TryDecodeVariableRepeatedFieldSet(pgnData, pgnDefinition, repeatedFields, out var variableRepeatedFieldsArray, out var variableRepeatedWarning))
                {
                    if (variableRepeatedFieldsArray.Count > 0)
                    {
                        jsonObject["RepeatedFields"] = variableRepeatedFieldsArray;
                    }

                    if (!string.IsNullOrWhiteSpace(variableRepeatedWarning))
                    {
                        AddDecodeWarning(warnings, variableRepeatedWarning);
                    }
                }
                else if (UsesUnsupportedRepeatedLayout(repeatedFields))
                {
                    AddDecodeWarning(warnings, "Repeated field set uses variable or unsupported field types; repeated values were not fully decoded.");
                }
                else
                {
                    var repeatedFieldsArray = new JsonArray();

                    try
                    {
                        ulong repetitionCount;
                        var repeatedSetBitLength = repeatedFields.Sum(field => field.BitLength);
                        if (pgnDefinition.RepeatingFieldSet1CountField > 0)
                        {
                            var countField = pgnDefinition.Fields[pgnDefinition.RepeatingFieldSet1CountField - 1];
                            int byteStart = countField.BitOffset / 8;
                            int bitStart = countField.BitOffset % 8;
                            int bitLength = countField.BitLength;

                            repetitionCount = ExtractBits(pgnData, byteStart, bitStart, bitLength);
                            if (IsUnavailableRepeatCount(repetitionCount, countField))
                            {
                                repetitionCount = 0;
                            }
                        }
                        else
                        {
                            var repeatedSetStartBitOffset = repeatedFields[0].BitOffset;
                            if (repeatedSetBitLength <= 0)
                            {
                                throw new ArgumentException("Repeated field set length must be positive.");
                            }

                            var remainingBits = Math.Max(0, (pgnData.Length * 8) - repeatedSetStartBitOffset);
                            repetitionCount = (ulong)(remainingBits / repeatedSetBitLength);
                        }

                        var maxPossibleRepetitions = 0UL;
                        if (repeatedFields.Length > 0)
                        {
                            var repeatedSetStartBitOffset = repeatedFields[0].BitOffset;
                            if (repeatedSetBitLength > 0)
                            {
                                var remainingBits = Math.Max(0, (pgnData.Length * 8) - repeatedSetStartBitOffset);
                                maxPossibleRepetitions = (ulong)(remainingBits / repeatedSetBitLength);
                            }
                        }

                        if (maxPossibleRepetitions > 0 && repetitionCount > maxPossibleRepetitions)
                        {
                            repetitionCount = maxPossibleRepetitions;
                        }

                        for (int i = 0; i < (int)repetitionCount; i++)
                        {
                            var repeatedFieldSet = new JsonObject();

                            for (int j = 0; j < repeatedFieldCount; j++)
                            {
                                var field = pgnDefinition.Fields[repeatedFieldStart + j];

                                try
                                {
                                    var repeatedSetRelativeOffset = field.BitOffset - repeatedFields[0].BitOffset;
                                    int repeatBitOffset = repeatedFields[0].BitOffset + (i * repeatedSetBitLength) + repeatedSetRelativeOffset;
                                    int repeatByteStart = repeatBitOffset / 8;
                                    int repeatBitStart = repeatBitOffset % 8;
                                    int repeatBitLength = field.BitLength;

                                    ulong rawValue = ExtractBits(pgnData, repeatByteStart, repeatBitStart, repeatBitLength);
                                    object? decodedValue = DecodeFieldValue(rawValue, field, pgnData, pgnDefinition);

                                    if (decodedValue is double rangedValue &&
                                        ((field.RangeMin != null && rangedValue < field.RangeMin) ||
                                         (field.RangeMax != null && rangedValue > field.RangeMax)))
                                        continue;

                                    object? repeatedFinalValue = decodedValue == null ? null : ApplyUnitConversion(decodedValue, field);
                                    repeatedFieldSet[field.Name] = CreateJsonNode(repeatedFinalValue);
                                }
                                catch (Exception ex)
                                {
                                    AddDecodeWarning(warnings, $"Repeated field '{field.Name}' in item {i + 1} skipped: {ex.Message}");
                                    repeatedFieldSet[field.Name] = "<decode error>";
                                }
                            }

                            if (repeatedFieldSet.Count > 0)
                                repeatedFieldsArray.Add(repeatedFieldSet);
                        }
                    }
                    catch (Exception ex)
                    {
                        AddDecodeWarning(warnings, $"Repeated field set could not be decoded: {ex.Message}");
                    }

                    if (repeatedFieldsArray.Count > 0)
                    {
                        jsonObject["RepeatedFields"] = repeatedFieldsArray;
                    }
                }
            }

            AddVictronRegisterDecode(jsonObject, pgnDefinition, pgnData);

            if (warnings.Count > 0)
            {
                jsonObject["Warnings"] = warnings;
            }

            return jsonObject;
        }

        private static int GetEffectiveBitOffset(Canboat.Field field, int currentBitCursor)
        {
            if (field.BitLengthVariable || field.FieldType == "STRING_LZ" || field.FieldType == "STRING_LAU")
            {
                return field.BitOffset > 0 ? field.BitOffset : currentBitCursor;
            }

            return field.BitOffset;
        }

        private static int AdvanceBitCursor(int currentBitCursor, int effectiveBitOffset, int bitsConsumed)
        {
            if (bitsConsumed <= 0)
            {
                return currentBitCursor;
            }

            return Math.Max(currentBitCursor, effectiveBitOffset + bitsConsumed);
        }

        private static void AddVictronRegisterDecode(JsonObject jsonObject, Canboat.Pgn pgnDefinition, byte[] pgnData)
        {
            if (pgnDefinition.PGN != 61184 || pgnData.Length < 8)
            {
                return;
            }

            var manufacturerCode = ExtractBits(pgnData, 0, 0, 11);
            var industryCode = ExtractBits(pgnData, 0, 13, 3);
            if (manufacturerCode != 358 || industryCode != 4)
            {
                return;
            }

            var registerId = (ushort)ExtractBits(pgnData, 2, 0, 16);
            var payload = (uint)ExtractBits(pgnData, 4, 0, 32);
            var decodedRegister = VictronRegisters.Decode(registerId, payload);
            if (decodedRegister == null)
            {
                return;
            }

            var fields = jsonObject["Fields"] as JsonArray;
            fields?.Add(new JsonObject
            {
                ["Register"] = decodedRegister
            });
        }

        private static bool TryDecodeAlertValueRepeatedFieldSet(
            byte[] pgnData,
            Canboat.Pgn pgnDefinition,
            out JsonArray repeatedFieldsArray,
            out string? warning)
        {
            repeatedFieldsArray = new JsonArray();
            warning = null;

            if (pgnDefinition.PGN != 126988 ||
                pgnDefinition.RepeatingFieldSet1CountField <= 0 ||
                pgnDefinition.RepeatingFieldSet1StartField <= 0 ||
                pgnDefinition.RepeatingFieldSet1Size != 3)
            {
                return false;
            }

            try
            {
                var countField = pgnDefinition.Fields[pgnDefinition.RepeatingFieldSet1CountField - 1];
                var repetitionCount = (int)ExtractBits(
                    pgnData,
                    countField.BitOffset / 8,
                    countField.BitOffset % 8,
                    countField.BitLength);

                if (IsUnavailableRepeatCount((ulong)repetitionCount, countField) || repetitionCount <= 0)
                {
                    return true;
                }

                var repeatedStartField = pgnDefinition.Fields[pgnDefinition.RepeatingFieldSet1StartField - 1];
                var cursor = repeatedStartField.BitOffset / 8;

                for (var i = 0; i < repetitionCount; i++)
                {
                    if (cursor + 2 > pgnData.Length)
                    {
                        warning = $"Alert Value repeated field set ended early after {i} items.";
                        break;
                    }

                    var parameterNumber = pgnData[cursor++];
                    var valueDataFormat = pgnData[cursor++];
                    var valueDataStart = cursor;
                    var value = DecodeAlertValueData(pgnData, ref cursor, valueDataFormat);
                    var rawLength = Math.Max(0, cursor - valueDataStart);

                    var repeatedFieldSet = new JsonObject
                    {
                        ["Value Parameter Number"] = parameterNumber,
                        ["Value Data Format"] = FormatAlertValueDataFormat(valueDataFormat),
                        ["Value Data"] = CreateJsonNode(value)
                    };

                    if (rawLength > 0)
                    {
                        repeatedFieldSet["Value Data Raw"] = FormatPayloadSlice(pgnData, valueDataStart, rawLength);
                    }

                    repeatedFieldsArray.Add(repeatedFieldSet);
                }

                if (cursor < pgnData.Length)
                {
                    var trailingBytes = pgnData.AsSpan(cursor).ToArray();
                    if (trailingBytes.Any(value => value != 0x00 && value != 0xFF))
                    {
                        warning = $"Alert Value payload has {pgnData.Length - cursor} trailing byte(s): {FormatPayloadSlice(pgnData, cursor, pgnData.Length - cursor)}.";
                    }
                }
            }
            catch (Exception ex)
            {
                warning = $"Alert Value repeated field set could not be decoded: {ex.Message}";
            }

            return true;
        }

        private static object? DecodeAlertValueData(byte[] pgnData, ref int cursor, int valueDataFormat)
        {
            if (cursor >= pgnData.Length)
            {
                return null;
            }

            if (valueDataFormat == 0x32)
            {
                var valueField = new Canboat.Field
                {
                    Name = "Value Data",
                    FieldType = "STRING_LAU",
                    BitLength = 64,
                    BitOffset = cursor * 8
                };
                var decodedString = DecodeVariableStringField(pgnData, cursor * 8, valueField, allowTruncatedAtEnd: true);
                var consumedBytes = Math.Max(1, decodedString.BitsConsumed / 8);
                var availableStringBytes = pgnData.Length - cursor;
                cursor += Math.Max(consumedBytes, Math.Min(8, availableStringBytes));
                return decodedString.Value;
            }

            var availableBytes = Math.Min(8, pgnData.Length - cursor);
            if (availableBytes <= 0)
            {
                return null;
            }

            var rawValue = ExtractBits(pgnData, cursor, 0, availableBytes * 8);
            cursor += availableBytes;
            return rawValue;
        }

        private static string FormatAlertValueDataFormat(int valueDataFormat)
        {
            return valueDataFormat switch
            {
                0x32 => "STRING_LAU (50)",
                _ => valueDataFormat.ToString(CultureInfo.InvariantCulture)
            };
        }

        private static string FormatPayloadSlice(byte[] data, int start, int length)
        {
            if (start >= data.Length || length <= 0)
            {
                return string.Empty;
            }

            var safeLength = Math.Min(length, data.Length - start);
            return string.Join(" ", data.Skip(start).Take(safeLength).Select(value => $"0x{value:X2}"));
        }

        private static bool TryDecodeVariableRepeatedFieldSet(
            byte[] pgnData,
            Canboat.Pgn pgnDefinition,
            Canboat.Field[] repeatedFields,
            out JsonArray repeatedFieldsArray,
            out string? warning)
        {
            repeatedFieldsArray = new JsonArray();
            warning = null;

            if (repeatedFields.Length != 2 ||
                !string.Equals(repeatedFields[0].FieldType, "FIELD_INDEX", StringComparison.Ordinal) ||
                !string.Equals(repeatedFields[1].FieldType, "VARIABLE", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                var countField = pgnDefinition.Fields[pgnDefinition.RepeatingFieldSet1CountField - 1];
                int repetitionCount = (int)ExtractBits(pgnData, countField.BitOffset / 8, countField.BitOffset % 8, countField.BitLength);
                if (IsUnavailableRepeatCount((ulong)repetitionCount, countField))
                {
                    return true;
                }
                if (repetitionCount <= 0)
                {
                    return true;
                }

                var targetPgnField = pgnDefinition.Fields.FirstOrDefault(field => string.Equals(field.FieldType, "PGN", StringComparison.Ordinal));
                if (targetPgnField == null)
                {
                    warning = "Repeated field set could not be decoded: target PGN field not found.";
                    return true;
                }

                int targetPgn = (int)ExtractBits(pgnData, targetPgnField.BitOffset / 8, targetPgnField.BitOffset % 8, targetPgnField.BitLength);
                var targetDefinition = ResolveGroupFunctionTargetDefinition(targetPgn, pgnData, repeatedFields[0].BitOffset / 8, repetitionCount);
                if (targetDefinition == null)
                {
                    warning = $"Repeated field set could not be decoded: no matching target definition for PGN {targetPgn}.";
                    return true;
                }

                var cursor = repeatedFields[0].BitOffset / 8;
                for (var i = 0; i < repetitionCount; i++)
                {
                    if (cursor >= pgnData.Length)
                    {
                        warning = $"Repeated field set ended early after {i} items.";
                        break;
                    }

                    var parameterIndex = pgnData[cursor++];
                    if (parameterIndex <= 0 || parameterIndex > targetDefinition.Fields.Length)
                    {
                        warning = $"Repeated field set contains invalid parameter index {parameterIndex}.";
                        break;
                    }

                    var targetField = targetDefinition.Fields[parameterIndex - 1];
                    var decodedItem = DecodeGroupFunctionParameterValue(pgnData, ref cursor, targetField);
                    var repeatedFieldSet = new JsonObject
                    {
                        [targetField.Name] = CreateJsonNode(decodedItem)
                    };

                    repeatedFieldsArray.Add(repeatedFieldSet);
                }
            }
            catch (Exception ex)
            {
                warning = $"Repeated field set could not be decoded: {ex.Message}";
            }

            return true;
        }

        private static JsonNode? CreateJsonNode(object? value)
        {
            return value switch
            {
                null => null,
                JsonNode jsonNode => jsonNode,
                _ => JsonValue.Create(value)
            };
        }

        private static Canboat.Pgn? ResolveGroupFunctionTargetDefinition(int targetPgn, byte[] pgnData, int startByteOffset, int repetitionCount)
        {
            var candidates = ((App)Application.Current).CanboatRoot.PGNs
                .Where(candidate => candidate.PGN == targetPgn && candidate.Fields != null && candidate.Fields.Length > 0)
                .ToList();

            Canboat.Pgn? bestCandidate = null;
            int bestScore = int.MinValue;

            foreach (var candidate in candidates)
            {
                if (TryScoreGroupFunctionTargetDefinition(candidate, pgnData, startByteOffset, repetitionCount, out var score) &&
                    score > bestScore)
                {
                    bestScore = score;
                    bestCandidate = candidate;
                }
            }

            return bestCandidate;
        }

        private static bool TryScoreGroupFunctionTargetDefinition(Canboat.Pgn candidate, byte[] pgnData, int startByteOffset, int repetitionCount, out int score)
        {
            score = 0;
            var cursor = startByteOffset;

            for (var i = 0; i < repetitionCount; i++)
            {
                if (cursor >= pgnData.Length)
                {
                    return false;
                }

                var parameterIndex = pgnData[cursor++];
                if (parameterIndex <= 0 || parameterIndex > candidate.Fields.Length)
                {
                    return false;
                }

                var field = candidate.Fields[parameterIndex - 1];
                if (!TryReadGroupFunctionRawValue(pgnData, ref cursor, field, out var rawValue))
                {
                    return false;
                }

                score++;
                if (field.Match.HasValue)
                {
                    score += (int)rawValue == field.Match.Value ? 10 : -5;
                }
            }

            return true;
        }

        private static object? DecodeGroupFunctionParameterValue(byte[] pgnData, ref int cursor, Canboat.Field field)
        {
            if (string.Equals(field.FieldType, "STRING_LZ", StringComparison.Ordinal) ||
                string.Equals(field.FieldType, "STRING_LAU", StringComparison.Ordinal))
            {
                var decodedString = DecodeVariableStringField(pgnData, cursor * 8, field);
                cursor += decodedString.BitsConsumed / 8;
                return decodedString.Value;
            }

            if (!TryReadGroupFunctionRawValue(pgnData, ref cursor, field, out var rawValue))
            {
                throw new ArgumentException($"Not enough bytes to decode field '{field.Name}'.");
            }

            if (string.Equals(field.FieldType, "BINARY", StringComparison.Ordinal))
            {
                return FormatBinaryGroupFunctionValue(rawValue, field);
            }

            var decodedValue = DecodeFieldValue(rawValue, field);
            return decodedValue == null ? null : ApplyUnitConversion(decodedValue, field);
        }

        private static bool TryReadGroupFunctionRawValue(byte[] pgnData, ref int cursor, Canboat.Field field, out ulong rawValue)
        {
            rawValue = 0;

            if (field.BitLength <= 0)
            {
                return false;
            }

            var byteCount = Math.Max(1, (field.BitLength + 7) / 8);
            if (cursor + byteCount > pgnData.Length)
            {
                return false;
            }

            rawValue = ExtractBits(pgnData, cursor, 0, field.BitLength);
            cursor += byteCount;
            return true;
        }

        private static string FormatBinaryGroupFunctionValue(ulong rawValue, Canboat.Field field)
        {
            var byteCount = Math.Max(1, (field.BitLength + 7) / 8);
            var bytes = new byte[byteCount];

            for (var i = 0; i < byteCount; i++)
            {
                bytes[i] = (byte)((rawValue >> (i * 8)) & 0xFF);
            }

            return string.Join(" ", bytes.Select(b => $"0x{b:X2}"));
        }

        private static bool IsUnavailableRepeatCount(ulong rawValue, Canboat.Field countField)
        {
            if (countField.RangeMax != null && rawValue > (ulong)countField.RangeMax.Value)
            {
                return true;
            }

            return false;
        }

        private static bool UsesUnsupportedRepeatedLayout(IEnumerable<Canboat.Field> repeatedFields)
        {
            foreach (var field in repeatedFields)
            {
                if (field.BitLength <= 0)
                {
                    return true;
                }

                switch (field.FieldType)
                {
                    case "VARIABLE":
                    case "KEY_VALUE":
                    case "STRING_LZ":
                    case "STRING_LAU":
                    case "BINARY":
                        return true;
                }
            }

            return false;
        }

        private static void AddDecodeWarning(JsonArray warnings, string warning)
        {
            if (!warnings.Any(node => string.Equals(node?.GetValue<string>(), warning, StringComparison.Ordinal)))
            {
                warnings.Add(warning);
            }
        }

        private static (string? Value, int BitsConsumed) DecodeVariableStringField(
            byte[] pgnData,
            int bitOffset,
            Canboat.Field field,
            bool allowTruncatedAtEnd = false)
        {
            if (bitOffset % 8 != 0)
            {
                throw new NotSupportedException($"Field type '{field.FieldType}' requires byte-aligned data.");
            }

            var byteOffset = bitOffset / 8;
            if (byteOffset >= pgnData.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(bitOffset), "String field starts beyond the end of the PGN payload.");
            }

            return field.FieldType switch
            {
                "STRING_LZ" => DecodeStringLz(pgnData, byteOffset),
                "STRING_LAU" => DecodeStringLau(pgnData, byteOffset, allowTruncatedAtEnd),
                _ => throw new NotSupportedException($"Field type '{field.FieldType}' is not supported by variable string decoder.")
            };
        }

        private static (string? Value, int BitsConsumed) DecodeStringLz(byte[] pgnData, int byteOffset)
        {
            var declaredLength = pgnData[byteOffset];
            if (declaredLength == byte.MaxValue)
            {
                return (null, 8);
            }

            var payloadStart = byteOffset + 1;
            if (payloadStart + declaredLength > pgnData.Length)
            {
                throw new ArgumentException("STRING_LZ length extends beyond the PGN payload.");
            }

            var payload = pgnData.AsSpan(payloadStart, declaredLength);
            var text = Encoding.UTF8.GetString(TrimTrailingZeros(payload));
            var totalBytes = 1 + declaredLength;

            if (payloadStart + declaredLength < pgnData.Length && pgnData[payloadStart + declaredLength] == 0x00)
            {
                totalBytes++;
            }

            return (text, totalBytes * 8);
        }

        private static (string? Value, int BitsConsumed) DecodeStringLau(byte[] pgnData, int byteOffset, bool allowTruncatedAtEnd)
        {
            var declaredCount = pgnData[byteOffset];
            if (declaredCount == byte.MaxValue)
            {
                return (null, 8);
            }

            if (declaredCount < 2)
            {
                throw new ArgumentException("STRING_LAU count must be at least 2.");
            }

            var availableCount = pgnData.Length - byteOffset;
            var effectiveCount = declaredCount;

            if (byteOffset + declaredCount > pgnData.Length)
            {
                if (!allowTruncatedAtEnd)
                {
                    throw new ArgumentException("STRING_LAU length extends beyond the PGN payload.");
                }

                // Some AIS payloads appear to carry a trailing STRING_LAU with an
                // overstated count byte. When the string is the final field, consume
                // the remaining payload instead of failing the whole field.
                effectiveCount = (byte)availableCount;
                if (effectiveCount < 2)
                {
                    return (string.Empty, Math.Max(1, (int)effectiveCount) * 8);
                }
            }

            var encodingType = pgnData[byteOffset + 1];
            var payloadLength = effectiveCount - 2;
            var payloadStart = byteOffset + 2;
            var payload = pgnData.AsSpan(payloadStart, payloadLength);

            string value = encodingType switch
            {
                0 => DecodeUnicodeLau(payload),
                1 => Encoding.UTF8.GetString(TrimTrailingZeros(payload)),
                _ => throw new NotSupportedException($"Unsupported STRING_LAU encoding type '{encodingType}'.")
            };

            var totalBytes = effectiveCount;
            if (encodingType == 0)
            {
                if (payloadStart + payloadLength + 1 < pgnData.Length &&
                    pgnData[payloadStart + payloadLength] == 0x00 &&
                    pgnData[payloadStart + payloadLength + 1] == 0x00)
                {
                    totalBytes += 2;
                }
            }
            else if (payloadStart + payloadLength < pgnData.Length && pgnData[payloadStart + payloadLength] == 0x00)
            {
                totalBytes++;
            }

            return (value, totalBytes * 8);
        }

        private static string DecodeUnicodeLau(ReadOnlySpan<byte> payload)
        {
            var trimmedPayload = TrimTrailingZeros(payload);
            if ((trimmedPayload.Length & 1) == 1)
            {
                trimmedPayload = trimmedPayload[..^1];
            }

            return Encoding.Unicode.GetString(trimmedPayload);
        }

        private static byte[] TrimTrailingZeros(ReadOnlySpan<byte> payload)
        {
            var end = payload.Length;
            while (end > 0 && payload[end - 1] == 0x00)
            {
                end--;
            }

            return payload[..end].ToArray();
        }

        private static string DecodeDynamicFieldKey(ulong rawValue, Canboat.Field field)
        {
            var definition = LookupFieldTypeDefinition(field.LookupFieldTypeEnumeration, (int)rawValue);
            return definition?.name ?? $"Unknown ({rawValue})";
        }

        private static (object? Value, int BitsConsumed) DecodeDynamicFieldValue(
            byte[] pgnData,
            int bitOffset,
            Canboat.Field field,
            Canboat.Pgn pgnDefinition)
        {
            var keyField = FindPrecedingDynamicKeyField(pgnDefinition, field)
                ?? throw new InvalidOperationException("No preceding DYNAMIC_FIELD_KEY field was found.");

            var keyBitOffset = GetEffectiveBitOffset(keyField, 0);
            var keyRawValue = ExtractBits(pgnData, keyBitOffset / 8, keyBitOffset % 8, keyField.BitLength);
            var keyDefinition = LookupFieldTypeDefinition(keyField.LookupFieldTypeEnumeration, (int)keyRawValue);

            if (keyDefinition == null)
            {
                var fallbackBitLength = ResolveUnknownDynamicFieldBitLength(pgnData, bitOffset, pgnDefinition, field);
                var fallbackValue = DecodeUnknownDynamicFieldValue(pgnData, bitOffset, fallbackBitLength);
                return (fallbackValue, fallbackBitLength);
            }

            var bitLength = ResolveDynamicFieldBitLength(pgnData, keyDefinition, pgnDefinition, field);
            if (bitLength <= 0)
            {
                throw new InvalidOperationException($"Dynamic field '{field.Name}' has no valid bit length.");
            }

            var rawValue = ExtractBits(pgnData, bitOffset / 8, bitOffset % 8, bitLength);
            var syntheticField = CreateDynamicValueField(field, keyDefinition, bitLength);
            var decodedValue = DecodeFieldValue(rawValue, syntheticField, pgnData, pgnDefinition);
            var finalValue = decodedValue == null ? null : ApplyUnitConversion(decodedValue, syntheticField);

            return (finalValue, bitLength);
        }

        private static Canboat.Field? FindPrecedingDynamicKeyField(Canboat.Pgn pgnDefinition, Canboat.Field field)
        {
            return pgnDefinition.Fields
                .Where(candidate => candidate.Order < field.Order && string.Equals(candidate.FieldType, "DYNAMIC_FIELD_KEY", StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.Order)
                .FirstOrDefault();
        }

        private static Canboat.Enumfieldtypevalue? LookupFieldTypeDefinition(string enumeration, int value)
        {
            return ((App)Application.Current).CanboatRoot.LookupFieldTypeEnumerations
                .FirstOrDefault(item => item.Name == enumeration)?
                .EnumFieldTypeValues
                .FirstOrDefault(item => item.value == value);
        }

        private static int ResolveUnknownDynamicFieldBitLength(
            byte[] pgnData,
            int bitOffset,
            Canboat.Pgn pgnDefinition,
            Canboat.Field valueField)
        {
            var remainingBits = Math.Max(0, (pgnData.Length * 8) - bitOffset);
            if (remainingBits <= 0)
            {
                return 0;
            }

            var candidateLengthField = pgnDefinition.Fields
                .Where(candidate =>
                    candidate.Order < valueField.Order &&
                    string.Equals(candidate.FieldType, "NUMBER", StringComparison.Ordinal) &&
                    (string.Equals(candidate.Id, "length", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(candidate.Id, "minlength", StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrWhiteSpace(candidate.Name) && candidate.Name.Contains("length", StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(candidate => candidate.Order)
                .FirstOrDefault();

            if (candidateLengthField != null)
            {
                var lengthBitOffset = GetEffectiveBitOffset(candidateLengthField, 0);
                var rawLength = ExtractBits(pgnData, lengthBitOffset / 8, lengthBitOffset % 8, candidateLengthField.BitLength);
                if (rawLength > 0)
                {
                    var candidateBits = checked((int)Math.Min((ulong)remainingBits, rawLength * 8));
                    if (candidateBits > 0)
                    {
                        return candidateBits;
                    }
                }
            }

            return remainingBits;
        }

        private static object? DecodeUnknownDynamicFieldValue(byte[] pgnData, int bitOffset, int bitLength)
        {
            if (bitLength <= 0)
            {
                return null;
            }

            if (bitOffset % 8 != 0 || bitLength % 8 != 0)
            {
                var rawValue = ExtractBits(pgnData, bitOffset / 8, bitOffset % 8, bitLength);
                return rawValue;
            }

            var byteOffset = bitOffset / 8;
            var byteLength = bitLength / 8;
            if (byteOffset + byteLength > pgnData.Length)
            {
                byteLength = Math.Max(0, pgnData.Length - byteOffset);
            }

            if (byteLength <= 0)
            {
                return null;
            }

            if (byteLength == 1)
            {
                return pgnData[byteOffset];
            }

            var bytes = pgnData.AsSpan(byteOffset, byteLength).ToArray();
            return string.Join(" ", bytes.Select(b => $"0x{b:X2}"));
        }

        private static int ResolveDynamicFieldBitLength(
            byte[] pgnData,
            Canboat.Enumfieldtypevalue definition,
            Canboat.Pgn pgnDefinition,
            Canboat.Field valueField)
        {
            if (!string.IsNullOrWhiteSpace(definition.Bits) &&
                int.TryParse(definition.Bits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBits) &&
                parsedBits > 0)
            {
                return parsedBits;
            }

            var lengthField = pgnDefinition.Fields
                .Where(candidate => candidate.Order < valueField.Order && string.Equals(candidate.FieldType, "DYNAMIC_FIELD_LENGTH", StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.Order)
                .FirstOrDefault();

            if (lengthField == null)
            {
                return 0;
            }

            var lengthBitOffset = GetEffectiveBitOffset(lengthField, 0);
            var rawLength = ExtractBits(pgnData, lengthBitOffset / 8, lengthBitOffset % 8, lengthField.BitLength);
            return rawLength > 0 && rawLength <= int.MaxValue / 8
                ? (int)rawLength * 8
                : 0;
        }

        private static Canboat.Field CreateDynamicValueField(Canboat.Field valueField, Canboat.Enumfieldtypevalue definition, int bitLength)
        {
            var syntheticField = new Canboat.Field
            {
                Name = string.IsNullOrWhiteSpace(definition.name) ? valueField.Name : definition.name,
                FieldType = definition.FieldType,
                BitLength = bitLength,
                BitOffset = valueField.BitOffset,
                BitStart = valueField.BitStart,
                Resolution = definition.Resolution == 0 ? 1 : definition.Resolution,
                Signed = false,
                LookupEnumeration = definition.LookupEnumeration,
                LookupBitEnumeration = definition.LookupBitEnumeration,
                Unit = definition.Unit,
                Description = valueField.Description
            };

            return syntheticField;
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
        private static object? DecodeFieldValue(
            ulong rawValue,
            Canboat.Field field,
            byte[]? pgnData = null,
            Canboat.Pgn? pgnDefinition = null)
        {
            switch (field.FieldType)
            {
                case "NUMBER":
                case "MMSI":
                case "TIME":
                case "DATE":
                case "PGN":
                case "DURATION":
                case "FIELD_INDEX":
                    double decodedNumberValue = DecodeSignedOrUnsignedNumber(rawValue, field);

                    return decodedNumberValue;

                case "FLOAT":
                    return DecodeSignedOrUnsignedNumber(rawValue, field);

                case "DECIMAL":
                    return DecodeSignedOrUnsignedNumber(rawValue, field);

                case "ISO_NAME":
                    return DecodeIsoName(rawValue);

                case "LOOKUP":
                    return DecodeLookupValue(rawValue, field);

                case "INDIRECT_LOOKUP":
                    return DecodeIndirectLookupValue(rawValue, field, pgnData, pgnDefinition);
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
                default:
                    throw new NotSupportedException($"Field type '{field.FieldType}' is not supported.");
            }
            return null;

        }

        private static string DecodeLookupValue(ulong rawValue, Canboat.Field field)
        {
            var decoded = Lookup(field.LookupEnumeration, (int)rawValue);
            if (!decoded.StartsWith("Unknown", StringComparison.Ordinal))
            {
                return decoded;
            }

            return IsNotAvailableLookupValue(rawValue, field)
                ? "Not available"
                : decoded;
        }

        private static bool IsNotAvailableLookupValue(ulong rawValue, Canboat.Field field)
        {
            if (field.BitLength is <= 0 or >= 64)
            {
                return false;
            }

            var allBitsSet = (1UL << field.BitLength) - 1;
            if (rawValue == allBitsSet)
            {
                return true;
            }

            if (!field.RangeMax.HasValue)
            {
                return false;
            }

            var rangeMax = (ulong)field.RangeMax.Value;
            return rawValue >= rangeMax;
        }

        private static JsonObject DecodeIsoName(ulong rawValue)
        {
            var deviceClass = (int)ExtractRawBits(rawValue, 49, 7);

            return new JsonObject
            {
                ["Unique Number"] = (double)ExtractRawBits(rawValue, 0, 21),
                ["Manufacturer Code"] = Lookup("MANUFACTURER_CODE", (int)ExtractRawBits(rawValue, 21, 11)),
                ["Device Instance Lower"] = (double)ExtractRawBits(rawValue, 32, 3),
                ["Device Instance Upper"] = (double)ExtractRawBits(rawValue, 35, 5),
                ["Device Function"] = LookupIndirect("DEVICE_FUNCTION", deviceClass, (int)ExtractRawBits(rawValue, 40, 8)),
                ["Device Class"] = Lookup("DEVICE_CLASS", deviceClass),
                ["System Instance"] = (double)ExtractRawBits(rawValue, 56, 4),
                ["Industry Group"] = Lookup("INDUSTRY_CODE", (int)ExtractRawBits(rawValue, 60, 3)),
                ["Arbitrary address capable"] = Lookup("YES_NO", (int)ExtractRawBits(rawValue, 63, 1))
            };
        }

        private static ulong ExtractRawBits(ulong value, int bitOffset, int bitLength)
        {
            if (bitLength <= 0)
            {
                return 0;
            }

            if (bitLength >= 64)
            {
                return value >> bitOffset;
            }

            return (value >> bitOffset) & ((1UL << bitLength) - 1);
        }

        private static double DecodeSignedOrUnsignedNumber(ulong rawValue, Canboat.Field field)
        {
            if (!field.Signed || field.BitLength <= 1)
            {
                return ((double)rawValue + field.Offset) * field.Resolution;
            }

            if (field.BitLength >= 64)
            {
                return (unchecked((long)rawValue) + field.Offset) * field.Resolution;
            }

            var signBitMask = 1UL << (field.BitLength - 1);
            if ((rawValue & signBitMask) == 0)
            {
                return ((double)rawValue + field.Offset) * field.Resolution;
            }

            var valueMask = (1UL << field.BitLength) - 1;
            long signedValue = (long)(rawValue | ~valueMask);
            return (signedValue + field.Offset) * field.Resolution;
        }

        private static object DecodeIndirectLookupValue(
            ulong rawValue,
            Canboat.Field field,
            byte[]? pgnData,
            Canboat.Pgn? pgnDefinition)
        {
            if (pgnData == null || pgnDefinition?.Fields == null)
            {
                return $"Unknown ({rawValue})";
            }

            var fieldOrder = field.LookupIndirectEnumerationFieldOrder;
            if (fieldOrder <= 0 || fieldOrder > pgnDefinition.Fields.Length)
            {
                return $"Unknown ({rawValue})";
            }

            var indirectField = pgnDefinition.Fields[fieldOrder - 1];
            var indirectBitOffset = GetEffectiveBitOffset(indirectField, 0);
            var rawIndirectValue = ExtractBits(
                pgnData,
                indirectBitOffset / 8,
                indirectBitOffset % 8,
                indirectField.BitLength);

            return LookupIndirect(field.LookupIndirectEnumeration, (int)rawIndirectValue, (int)rawValue);
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
                numericValue = RoundByResolution(numericValue, field);

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
                    if (field.FieldType == "DATE" && numericValue <= 0)
                    {
                        return null;
                    }

                    return $"{DateTime.UnixEpoch.AddDays(numericValue):yyyy-MM-dd}";
                }
                else if (field.Unit == "deg")
                {
                    return $"{(int)numericValue} deg {Math.Abs((numericValue - (int)numericValue) * 60):F4} min";
                }

                else if (!string.IsNullOrEmpty(field.Unit))
                {
                    // If a unit exists but no special conversion is needed
                    return $"{numericValue.ToString(BuildFixedPointFormat(field), CultureInfo.InvariantCulture)} {field.Unit}";
                }

                return numericValue;
            }

            // If the value doesn't require conversion, return it as-is
            return decodedValue;
        }

        private static double RoundByResolution(double value, Canboat.Field field)
        {
            int decimals = GetDisplayDecimals(field);
            if (decimals <= 0)
            {
                return Math.Round(value, 0, MidpointRounding.AwayFromZero);
            }

            return Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        }

        private static string BuildFixedPointFormat(Canboat.Field field)
        {
            int decimals = GetDisplayDecimals(field);
            return decimals <= 0 ? "F0" : $"F{decimals}";
        }

        private static int GetDisplayDecimals(Canboat.Field field)
        {
            double resolution = Math.Abs(field.Resolution);
            if (resolution <= 0)
            {
                return 0;
            }

            int decimals = 0;
            while (resolution < 1 && decimals < 6)
            {
                resolution *= 10;
                decimals++;
            }

            return decimals;
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
            // PGN 60928: ISO Address Claim. Every NMEA 2000 device should send this,
            // so use it as the baseline device identity when Product Information is absent.
            var filteredRecords = records.Where(record => record.PGN == "60928").ToList();
            foreach (var record in filteredRecords)
            {
                var dataBytes = record.PayloadBytes;
                var pgnDefinition = ((App)Application.Current).CanboatRoot.PGNs
                    .FirstOrDefault(q => q.PGN.ToString() == record.PGN);
                var jsonObject = pgnDefinition == null ? null : DecodePgnData(dataBytes, pgnDefinition);

                if (jsonObject?["Fields"] is not JsonArray fieldsArray)
                {
                    continue;
                }

                var fields = fieldsArray.OfType<JsonObject>().ToList();
                var address = Convert.ToByte(record.Source);
                Globals.Devices[address] = new Device
                {
                    Address = address,
                    MfgCode = GetDecodedFieldString(fields, "Manufacturer Code"),
                    DeviceClass = GetDecodedFieldString(fields, "Device Class"),
                    DeviceFunction = GetDecodedFieldString(fields, "Device Function")
                };
            }

            // PGN 126996: Product Information. This is richer but not always present;
            // when it appears, merge it into the baseline 60928 device entry.
            filteredRecords = records.Where(record => record.PGN == "126996").ToList();
            foreach (var record in filteredRecords)
            {
                var dataBytes = record.PayloadBytes;
                var pgnDefinition = ((App)Application.Current).CanboatRoot.PGNs
                    .FirstOrDefault(q => q.PGN.ToString() == record.PGN);
                var jsonObject = pgnDefinition == null ? null : DecodePgnData(dataBytes, pgnDefinition);

                if (jsonObject?["Fields"] is not JsonArray fieldsArray)
                {
                    continue;
                }

                var fields = fieldsArray.OfType<JsonObject>().ToList();
                var address = Convert.ToByte(record.Source);
                if (!Globals.Devices.TryGetValue(address, out var dev))
                {
                    dev = new Device { Address = address };
                    Globals.Devices[address] = dev;
                }

                dev.ProductCode = GetDecodedFieldDouble(fields, "Product Code") ?? dev.ProductCode;
                dev.ModelID = GetDecodedFieldString(fields, "Model ID") ?? dev.ModelID;
                dev.SoftwareVersionCode = GetDecodedFieldString(fields, "Software Version Code") ?? dev.SoftwareVersionCode;
                dev.ModelVersion = GetDecodedFieldString(fields, "Model Version") ?? dev.ModelVersion;
                dev.ModelSerialCode = GetDecodedFieldString(fields, "Model Serial Code") ?? dev.ModelSerialCode;
                dev.MfgCode = GetDecodedFieldString(fields, "Manufacturer Code") ?? dev.MfgCode;
            }
        }

        private static string? GetDecodedFieldString(IEnumerable<JsonObject> fields, string fieldName)
        {
            var value = fields.FirstOrDefault(obj => obj.ContainsKey(fieldName))?[fieldName];
            if (value == null)
            {
                return null;
            }

            return value.GetValue<object>()?.ToString();
        }

        private static double? GetDecodedFieldDouble(IEnumerable<JsonObject> fields, string fieldName)
        {
            var value = fields.FirstOrDefault(obj => obj.ContainsKey(fieldName))?[fieldName];
            if (value == null)
            {
                return null;
            }

            if (value.GetValue<object>() is double doubleValue)
            {
                return doubleValue;
            }

            return double.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var parsedValue)
                ? parsedValue
                : null;
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
