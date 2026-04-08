using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;

namespace NMEA2000Analyzer
{
    internal static class PacketAnalysisService
    {
        public static async Task<JsonObject> OpenFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException($"File not found: {fullPath}");
            }

            if (Application.Current?.Dispatcher != null)
            {
                var loadTask = await Application.Current.Dispatcher.InvokeAsync(
                    () =>
                    {
                        if (Application.Current.MainWindow is not MainWindow mainWindow)
                        {
                            throw new InvalidOperationException("Main window is not available.");
                        }

                        return mainWindow.LoadFileFromMcpAsync(fullPath);
                    },
                    System.Windows.Threading.DispatcherPriority.Normal,
                    cancellationToken);
                await loadTask;
            }
            else
            {
                var result = await CaptureLoadService.LoadAsync(fullPath, cancellationToken: cancellationToken);
                ActiveDataSessionService.SetCurrent(
                    Path.GetFileName(fullPath),
                    result.Format.ToString(),
                    result.RawRecords,
                    result.AssembledRecords,
                    result.FirstTimestamp,
                    result.LastTimestamp,
                    fullPath);
            }

            return GetOpenDataSummary();
        }

        public static async Task<JsonObject> HighlightPacketsAsync(JsonObject arguments, CancellationToken cancellationToken = default)
        {
            var assembled = arguments["assembled"]?.GetValue<bool?>() ?? true;
            var seqNode = arguments["seqs"] as JsonArray
                ?? throw new InvalidOperationException("Missing required argument 'seqs'.");

            var sequences = seqNode
                .Select(node => node?.GetValue<int?>())
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToList();

            if (Application.Current?.Dispatcher == null)
            {
                throw new InvalidOperationException("Application dispatcher is not available.");
            }

            return await Application.Current.Dispatcher.InvokeAsync(
                () =>
                {
                    if (Application.Current.MainWindow is not MainWindow mainWindow)
                    {
                        throw new InvalidOperationException("Main window is not available.");
                    }

                    return mainWindow.HighlightPacketsFromMcp(sequences, assembled);
                },
                System.Windows.Threading.DispatcherPriority.Normal,
                cancellationToken);
        }

        public static JsonObject GetOpenDataSummary()
        {
            var session = RequireSession();
            return new JsonObject
            {
                ["name"] = session.Name,
                ["format"] = session.Format,
                ["sourcePath"] = session.SourcePath,
                ["rawCount"] = session.RawRecords.Count,
                ["assembledCount"] = session.AssembledRecords.Count,
                ["firstTimestamp"] = session.FirstTimestamp?.ToString("o"),
                ["lastTimestamp"] = session.LastTimestamp?.ToString("o"),
                ["updatedAt"] = session.UpdatedAt.ToString("o")
            };
        }

        public static JsonArray ListUnknownPgns(bool assembled = true)
        {
            var records = GetRecords(assembled);
            var groups = records
                .Select(record =>
                {
                    var decoded = TryDecodeRecord(record);
                    return new
                    {
                        Record = record,
                        Decoded = decoded,
                        Reasons = GetReverseEngineeringReasons(record, decoded)
                    };
                })
                .Where(item => item.Reasons.Count > 0)
                .GroupBy(item => item.Record.PGN ?? string.Empty)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal);

            var result = new JsonArray();
            foreach (var group in groups)
            {
                var first = group.First().Record;
                var sources = group
                    .Select(item => item.Record.Source)
                    .Where(source => !string.IsNullOrWhiteSpace(source))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(source => source, StringComparer.Ordinal)
                    .ToList();
                var reasons = group
                    .SelectMany(item => item.Reasons)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(reason => reason, StringComparer.Ordinal)
                    .ToList();

                result.Add(new JsonObject
                {
                    ["pgn"] = first.PGN ?? string.Empty,
                    ["description"] = first.Description ?? string.Empty,
                    ["count"] = group.Count(),
                    ["sources"] = new JsonArray(sources.Select(source => JsonValue.Create(source)).ToArray()),
                    ["reasons"] = new JsonArray(reasons.Select(reason => JsonValue.Create(reason)).ToArray())
                });
            }

            return result;
        }

        public static JsonArray QueryPackets(JsonObject arguments)
        {
            var assembled = arguments["assembled"]?.GetValue<bool?>() ?? true;
            var includeDecoded = arguments["include_decoded"]?.GetValue<bool?>() ?? false;
            var onlyUnknown = arguments["only_unknown"]?.GetValue<bool?>() ?? false;
            var onlyWithWarnings = arguments["only_with_warnings"]?.GetValue<bool?>() ?? false;
            var limit = Math.Clamp(arguments["limit"]?.GetValue<int?>() ?? 50, 1, 500);
            var offset = Math.Max(0, arguments["offset"]?.GetValue<int?>() ?? 0);
            var pgn = arguments["pgn"]?.ToString();
            var src = arguments["src"]?.ToString();
            var dst = arguments["dst"]?.ToString();

            var records = GetRecords(assembled)
                .Where(record => string.IsNullOrWhiteSpace(pgn) || string.Equals(record.PGN, pgn, StringComparison.Ordinal))
                .Where(record => string.IsNullOrWhiteSpace(src) || string.Equals(record.Source, src, StringComparison.Ordinal))
                .Where(record => string.IsNullOrWhiteSpace(dst) || string.Equals(record.Destination, dst, StringComparison.Ordinal));

            var result = new JsonArray();
            foreach (var record in records.Skip(offset))
            {
                var decoded = includeDecoded || onlyUnknown || onlyWithWarnings ? TryDecodeRecord(record) : null;
                var hasWarnings = decoded?["Warnings"] is JsonArray warningArray && warningArray.Count > 0;
                var isUnknown = IsUnknownRecord(record, decoded);

                if (onlyUnknown && !isUnknown)
                {
                    continue;
                }

                if (onlyWithWarnings && !hasWarnings)
                {
                    continue;
                }

                var packet = BuildPacketJson(record, decoded, includeDecoded);
                result.Add(packet);

                if (result.Count >= limit)
                {
                    break;
                }
            }

            return result;
        }

        public static async Task<JsonArray> GetSelectedPacketsAsync(CancellationToken cancellationToken = default)
        {
            if (Application.Current?.Dispatcher == null)
            {
                throw new InvalidOperationException("Application dispatcher is not available.");
            }

            var records = await Application.Current.Dispatcher.InvokeAsync(
                () =>
                {
                    if (Application.Current.MainWindow is not MainWindow mainWindow)
                    {
                        throw new InvalidOperationException("Main window is not available.");
                    }

                    return mainWindow.GetSelectedPacketsForMcp();
                },
                System.Windows.Threading.DispatcherPriority.Normal,
                cancellationToken);

            return new JsonArray(records.Select(record => BuildPacketJson(record, TryDecodeRecord(record), includeDecoded: true)).ToArray());
        }

        public static async Task<JsonObject?> GetCurrentPacketAsync(CancellationToken cancellationToken = default)
        {
            if (Application.Current?.Dispatcher == null)
            {
                throw new InvalidOperationException("Application dispatcher is not available.");
            }

            var record = await Application.Current.Dispatcher.InvokeAsync(
                () =>
                {
                    if (Application.Current.MainWindow is not MainWindow mainWindow)
                    {
                        throw new InvalidOperationException("Main window is not available.");
                    }

                    return mainWindow.GetCurrentPacketForMcp();
                },
                System.Windows.Threading.DispatcherPriority.Normal,
                cancellationToken);

            return record == null
                ? null
                : BuildPacketJson(record, TryDecodeRecord(record), includeDecoded: true);
        }

        public static JsonObject GetPacketContext(JsonObject arguments)
        {
            var assembled = arguments["assembled"]?.GetValue<bool?>() ?? true;
            var before = Math.Clamp(arguments["before"]?.GetValue<int?>() ?? 5, 0, 100);
            var after = Math.Clamp(arguments["after"]?.GetValue<int?>() ?? 5, 0, 100);
            var seq = arguments["seq"]?.GetValue<int?>()
                ?? throw new InvalidOperationException("Missing required argument 'seq'.");

            var records = GetRecords(assembled);
            var index = records.FindIndex(record => record.LogSequenceNumber == seq);
            if (index < 0)
            {
                throw new InvalidOperationException($"Packet with seq {seq} was not found.");
            }

            var start = Math.Max(0, index - before);
            var end = Math.Min(records.Count - 1, index + after);
            var context = new JsonArray();
            for (var i = start; i <= end; i++)
            {
                var item = BuildPacketJson(records[i], decoded: null, includeDecoded: false);
                item["isTarget"] = i == index;
                context.Add(item);
            }

            return new JsonObject
            {
                ["targetSeq"] = seq,
                ["assembled"] = assembled,
                ["context"] = context
            };
        }

        public static JsonObject GetByteColumns(JsonObject arguments)
        {
            var assembled = arguments["assembled"]?.GetValue<bool?>() ?? true;
            var pgn = arguments["pgn"]?.ToString();
            if (string.IsNullOrWhiteSpace(pgn))
            {
                throw new InvalidOperationException("Missing required argument 'pgn'.");
            }

            var src = arguments["src"]?.ToString();
            var records = GetRecords(assembled)
                .Where(record => string.Equals(record.PGN, pgn, StringComparison.Ordinal))
                .Where(record => string.IsNullOrWhiteSpace(src) || string.Equals(record.Source, src, StringComparison.Ordinal))
                .ToList();

            var columns = new JsonArray();
            if (records.Count == 0)
            {
                return new JsonObject
                {
                    ["pgn"] = pgn,
                    ["assembled"] = assembled,
                    ["count"] = 0,
                    ["columns"] = columns
                };
            }

            var maxLength = records.Max(record => record.PayloadBytes.Length);
            for (var columnIndex = 0; columnIndex < maxLength; columnIndex++)
            {
                var values = records
                    .Where(record => record.PayloadBytes.Length > columnIndex)
                    .Select(record => record.PayloadBytes[columnIndex])
                    .ToList();

                var histogram = values
                    .GroupBy(value => value)
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key)
                    .ToList();

                columns.Add(new JsonObject
                {
                    ["index"] = columnIndex,
                    ["presentCount"] = values.Count,
                    ["distinctCount"] = histogram.Count,
                    ["constantValue"] = histogram.Count == 1 ? $"0x{histogram[0].Key:X2}" : null,
                    ["mostCommonValue"] = histogram.Count > 0 ? $"0x{histogram[0].Key:X2}" : null,
                    ["topValues"] = new JsonArray(histogram
                        .Take(8)
                        .Select(group => new JsonObject
                        {
                            ["value"] = $"0x{group.Key:X2}",
                            ["count"] = group.Count()
                        })
                        .ToArray())
                });
            }

            return new JsonObject
            {
                ["pgn"] = pgn,
                ["assembled"] = assembled,
                ["count"] = records.Count,
                ["columns"] = columns
            };
        }

        public static JsonObject GroupPacketVariants(JsonObject arguments)
        {
            var assembled = arguments["assembled"]?.GetValue<bool?>() ?? true;
            var pgn = arguments["pgn"]?.ToString();
            if (string.IsNullOrWhiteSpace(pgn))
            {
                throw new InvalidOperationException("Missing required argument 'pgn'.");
            }

            var src = arguments["src"]?.ToString();
            var limit = Math.Clamp(arguments["limit"]?.GetValue<int?>() ?? 20, 1, 200);

            var records = GetRecords(assembled)
                .Where(record => string.Equals(record.PGN, pgn, StringComparison.Ordinal))
                .Where(record => string.IsNullOrWhiteSpace(src) || string.Equals(record.Source, src, StringComparison.Ordinal))
                .ToList();

            var variants = records
                .GroupBy(record => record.Data ?? string.Empty, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Take(limit)
                .Select(group =>
                {
                    var first = group.First();
                    return new JsonObject
                    {
                        ["raw"] = group.Key,
                        ["count"] = group.Count(),
                        ["length"] = first.PayloadBytes.Length,
                        ["sources"] = new JsonArray(group.Select(record => record.Source)
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(value => value, StringComparer.Ordinal)
                            .Select(value => JsonValue.Create(value))
                            .ToArray()),
                        ["sampleSeqs"] = new JsonArray(group.Select(record => JsonValue.Create(record.LogSequenceNumber)).Take(5).ToArray()),
                        ["firstTimestamp"] = group.Select(record => ParseTimestamp(record.Timestamp)).Where(value => value.HasValue).Min()?.ToString("o"),
                        ["lastTimestamp"] = group.Select(record => ParseTimestamp(record.Timestamp)).Where(value => value.HasValue).Max()?.ToString("o")
                    };
                })
                .ToArray();

            return new JsonObject
            {
                ["pgn"] = pgn,
                ["assembled"] = assembled,
                ["count"] = records.Count,
                ["variantCount"] = variants.Length,
                ["variants"] = new JsonArray(variants)
            };
        }

        public static JsonObject FindCorrelatedPackets(JsonObject arguments)
        {
            var assembled = arguments["assembled"]?.GetValue<bool?>() ?? true;
            var sameSourceOnly = arguments["same_source_only"]?.GetValue<bool?>() ?? true;
            var limit = Math.Clamp(arguments["limit"]?.GetValue<int?>() ?? 20, 1, 200);
            var windowMs = Math.Clamp(arguments["window_ms"]?.GetValue<int?>() ?? 250, 1, 60000);
            var seq = arguments["seq"]?.GetValue<int?>();
            var pgn = arguments["pgn"]?.ToString();
            var src = arguments["src"]?.ToString();

            var records = GetRecords(assembled);
            MainWindow.Nmea2000Record? targetRecord = null;

            if (seq.HasValue)
            {
                targetRecord = records.FirstOrDefault(record => record.LogSequenceNumber == seq.Value)
                    ?? throw new InvalidOperationException($"Packet with seq {seq.Value} was not found.");
            }
            else if (!string.IsNullOrWhiteSpace(pgn))
            {
                targetRecord = records
                    .Where(record => string.Equals(record.PGN, pgn, StringComparison.Ordinal))
                    .Where(record => string.IsNullOrWhiteSpace(src) || string.Equals(record.Source, src, StringComparison.Ordinal))
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException($"No packet was found for PGN {pgn}.");
            }
            else
            {
                throw new InvalidOperationException("Specify either 'seq' or 'pgn'.");
            }

            var targetTimestamp = ParseTimestamp(targetRecord.Timestamp)
                ?? throw new InvalidOperationException("Target packet has no usable timestamp.");

            var correlatedGroups = records
                .Where(record => record.LogSequenceNumber != targetRecord.LogSequenceNumber)
                .Where(record =>
                {
                    var timestamp = ParseTimestamp(record.Timestamp);
                    if (!timestamp.HasValue)
                    {
                        return false;
                    }

                    var delta = Math.Abs((timestamp.Value - targetTimestamp).TotalMilliseconds);
                    if (delta > windowMs)
                    {
                        return false;
                    }

                    if (sameSourceOnly && !string.Equals(record.Source, targetRecord.Source, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    return true;
                })
                .GroupBy(record => new { record.PGN, record.Description, record.Source })
                .Select(group =>
                {
                    var nearestDelta = group
                        .Select(record => Math.Abs((ParseTimestamp(record.Timestamp)!.Value - targetTimestamp).TotalMilliseconds))
                        .DefaultIfEmpty(double.MaxValue)
                        .Min();

                    return new
                    {
                        group.Key.PGN,
                        group.Key.Description,
                        group.Key.Source,
                        Count = group.Count(),
                        NearestDeltaMs = nearestDelta,
                        SampleSeqs = group.Select(record => record.LogSequenceNumber).Take(5).ToArray()
                    };
                })
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.NearestDeltaMs)
                .ThenBy(group => group.PGN, StringComparer.Ordinal)
                .Take(limit)
                .ToArray();

            return new JsonObject
            {
                ["target"] = BuildPacketJson(targetRecord, decoded: null, includeDecoded: false),
                ["assembled"] = assembled,
                ["windowMs"] = windowMs,
                ["sameSourceOnly"] = sameSourceOnly,
                ["correlated"] = new JsonArray(correlatedGroups.Select(group => new JsonObject
                {
                    ["pgn"] = group.PGN ?? string.Empty,
                    ["description"] = group.Description ?? string.Empty,
                    ["src"] = group.Source ?? string.Empty,
                    ["count"] = group.Count,
                    ["nearestDeltaMs"] = Math.Round(group.NearestDeltaMs, 3),
                    ["sampleSeqs"] = new JsonArray(group.SampleSeqs.Select(value => JsonValue.Create(value)).ToArray())
                }).ToArray())
            };
        }

        public static JsonObject ComparePacketPair(JsonObject arguments)
        {
            var assembled = arguments["assembled"]?.GetValue<bool?>() ?? true;
            var seqA = arguments["seq_a"]?.GetValue<int?>()
                ?? throw new InvalidOperationException("Missing required argument 'seq_a'.");
            var seqB = arguments["seq_b"]?.GetValue<int?>()
                ?? throw new InvalidOperationException("Missing required argument 'seq_b'.");

            var records = GetRecords(assembled);
            var recordA = records.FirstOrDefault(record => record.LogSequenceNumber == seqA)
                ?? throw new InvalidOperationException($"Packet with seq {seqA} was not found.");
            var recordB = records.FirstOrDefault(record => record.LogSequenceNumber == seqB)
                ?? throw new InvalidOperationException($"Packet with seq {seqB} was not found.");

            var maxLength = Math.Max(recordA.PayloadBytes.Length, recordB.PayloadBytes.Length);
            var byteDiffs = new JsonArray();
            for (var i = 0; i < maxLength; i++)
            {
                byte? valueA = i < recordA.PayloadBytes.Length ? recordA.PayloadBytes[i] : null;
                byte? valueB = i < recordB.PayloadBytes.Length ? recordB.PayloadBytes[i] : null;
                if (valueA == valueB)
                {
                    continue;
                }

                byteDiffs.Add(new JsonObject
                {
                    ["index"] = i,
                    ["a"] = valueA.HasValue ? $"0x{valueA.Value:X2}" : null,
                    ["b"] = valueB.HasValue ? $"0x{valueB.Value:X2}" : null
                });
            }

            return new JsonObject
            {
                ["assembled"] = assembled,
                ["packetA"] = BuildPacketJson(recordA, TryDecodeRecord(recordA), includeDecoded: true),
                ["packetB"] = BuildPacketJson(recordB, TryDecodeRecord(recordB), includeDecoded: true),
                ["samePgn"] = string.Equals(recordA.PGN, recordB.PGN, StringComparison.Ordinal),
                ["sameSource"] = string.Equals(recordA.Source, recordB.Source, StringComparison.Ordinal),
                ["byteDiffs"] = byteDiffs
            };
        }

        public static async Task<JsonObject> SetPgnFiltersAsync(JsonObject arguments, CancellationToken cancellationToken = default)
        {
            if (Application.Current?.Dispatcher == null)
            {
                throw new InvalidOperationException("Application dispatcher is not available.");
            }

            var include = ReadStringArray(arguments["include_pgns"]);
            var exclude = ReadStringArray(arguments["exclude_pgns"]);

            return await Application.Current.Dispatcher.InvokeAsync(
                () =>
                {
                    if (Application.Current.MainWindow is not MainWindow mainWindow)
                    {
                        throw new InvalidOperationException("Main window is not available.");
                    }

                    return mainWindow.SetPgnFiltersFromMcp(include, exclude);
                },
                System.Windows.Threading.DispatcherPriority.Normal,
                cancellationToken);
        }

        public static async Task<JsonObject> ClearFiltersAsync(CancellationToken cancellationToken = default)
        {
            if (Application.Current?.Dispatcher == null)
            {
                throw new InvalidOperationException("Application dispatcher is not available.");
            }

            return await Application.Current.Dispatcher.InvokeAsync(
                () =>
                {
                    if (Application.Current.MainWindow is not MainWindow mainWindow)
                    {
                        throw new InvalidOperationException("Main window is not available.");
                    }

                    return mainWindow.ClearFiltersFromMcp();
                },
                System.Windows.Threading.DispatcherPriority.Normal,
                cancellationToken);
        }

        private static List<MainWindow.Nmea2000Record> GetRecords(bool assembled)
        {
            var session = RequireSession();
            return (assembled ? session.AssembledRecords : session.RawRecords).ToList();
        }

        private static ActiveDataSession RequireSession()
        {
            return ActiveDataSessionService.GetCurrent()
                ?? throw new InvalidOperationException("No open data is loaded.");
        }

        private static IReadOnlyList<string> GetReverseEngineeringReasons(MainWindow.Nmea2000Record record, JsonObject? decoded)
        {
            var reasons = new List<string>();

            if (IsUnknownRecord(record, decoded))
            {
                reasons.Add("unknown");
            }

            if (decoded?["Warnings"] is JsonArray warningArray && warningArray.Count > 0)
            {
                reasons.Add("decode_warnings");
            }

            if (IsProprietaryRecord(record))
            {
                reasons.Add("proprietary");
            }

            return reasons;
        }

        private static bool IsUnknownRecord(MainWindow.Nmea2000Record record, JsonObject? decoded)
        {
            return string.Equals(record.Description, "No pattern match", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.Description, "Unknown PGN", StringComparison.OrdinalIgnoreCase)
                || decoded == null;
        }

        private static bool IsProprietaryRecord(MainWindow.Nmea2000Record record)
        {
            var description = record.Description ?? string.Empty;
            return description.StartsWith("MFG -", StringComparison.OrdinalIgnoreCase)
                || description.StartsWith("MFG-Specific", StringComparison.OrdinalIgnoreCase)
                || description.StartsWith("MFG Specific", StringComparison.OrdinalIgnoreCase)
                || description.IndexOf("Proprietary", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static JsonObject BuildPacketJson(MainWindow.Nmea2000Record record, JsonObject? decoded, bool includeDecoded)
        {
            var packet = new JsonObject
            {
                ["seq"] = record.LogSequenceNumber,
                ["timestamp"] = record.Timestamp ?? string.Empty,
                ["src"] = record.Source ?? string.Empty,
                ["dst"] = record.Destination ?? string.Empty,
                ["pgn"] = record.PGN ?? string.Empty,
                ["description"] = record.Description ?? string.Empty,
                ["type"] = record.Type ?? string.Empty,
                ["priority"] = record.Priority ?? string.Empty,
                ["raw"] = record.Data ?? string.Empty,
                ["device"] = record.DeviceInfo ?? string.Empty
            };

            if (includeDecoded)
            {
                packet["decoded"] = decoded?.DeepClone();
            }
            else if (decoded?["Warnings"] is JsonArray warnings)
            {
                packet["warnings"] = warnings.DeepClone();
            }

            return packet;
        }

        private static JsonObject? TryDecodeRecord(MainWindow.Nmea2000Record record)
        {
            var app = System.Windows.Application.Current as App;
            var root = app?.CanboatRoot;
            if (root?.PGNs == null)
            {
                return null;
            }

            try
            {
                if (record.PGNListIndex.HasValue &&
                    record.PGNListIndex.Value >= 0 &&
                    record.PGNListIndex.Value < root.PGNs.Count)
                {
                    return PgnDefinitions.DecodePgnData(record.PayloadBytes, root.PGNs[record.PGNListIndex.Value]);
                }

                var pgnDefinition = root.PGNs.FirstOrDefault(pgn =>
                    string.Equals(pgn.PGN.ToString(CultureInfo.InvariantCulture), record.PGN, StringComparison.Ordinal));

                return pgnDefinition == null
                    ? null
                    : PgnDefinitions.DecodePgnData(record.PayloadBytes, pgnDefinition);
            }
            catch
            {
                return null;
            }
        }

        private static DateTimeOffset? ParseTimestamp(string? timestamp)
        {
            if (string.IsNullOrWhiteSpace(timestamp))
            {
                return null;
            }

            return DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
        }

        private static IReadOnlyList<string> ReadStringArray(JsonNode? node)
        {
            if (node is JsonArray array)
            {
                return array
                    .Select(item => item?.ToString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToList();
            }

            var single = node?.ToString();
            if (string.IsNullOrWhiteSpace(single))
            {
                return Array.Empty<string>();
            }

            return single
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
        }
    }
}
