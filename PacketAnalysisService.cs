using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Newtonsoft.Json.Linq;

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

        public static async Task<JsonObject> ReloadDefinitionsAsync(CancellationToken cancellationToken = default)
        {
            if (Application.Current?.Dispatcher == null)
            {
                throw new InvalidOperationException("Application dispatcher is not available.");
            }

            var reloadTask = await Application.Current.Dispatcher.InvokeAsync(
                () =>
                {
                    if (Application.Current.MainWindow is not MainWindow mainWindow)
                    {
                        throw new InvalidOperationException("Main window is not available.");
                    }

                    return mainWindow.ReloadDefinitionsFromMcpAsync();
                },
                System.Windows.Threading.DispatcherPriority.Normal,
                cancellationToken);

            return await reloadTask;
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

        public static JsonObject ListDevices()
        {
            var session = RequireSession();
            var rawRecords = session.RawRecords;
            var assembledRecords = session.AssembledRecords;

            var observedAddresses = rawRecords
                .Concat(assembledRecords)
                .Select(record => record.Source)
                .Where(source => int.TryParse(source, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                .Select(source => int.Parse(source, CultureInfo.InvariantCulture))
                .Distinct()
                .OrderBy(address => address)
                .ToList();

            var unassembledCounts = rawRecords
                .GroupBy(record => record.Source ?? string.Empty)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            var assembledCounts = assembledRecords
                .GroupBy(record => record.Source ?? string.Empty)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            var throughputBySource = assembledRecords
                .GroupBy(record => record.Source ?? string.Empty)
                .ToDictionary(group => group.Key, CalculateThroughput, StringComparer.Ordinal);

            var addresses = observedAddresses
                .Concat(Globals.Devices.Keys)
                .Distinct()
                .OrderBy(address => address);

            var devices = new JsonArray();
            double totalAverageBytesPerSecond = 0;
            double totalPeakBytesPerSecond = 0;
            var totalUnassembledCount = 0;
            var totalAssembledCount = 0;

            foreach (var address in addresses)
            {
                var sourceKey = address.ToString(CultureInfo.InvariantCulture);
                throughputBySource.TryGetValue(sourceKey, out var throughput);
                Globals.Devices.TryGetValue(address, out var deviceInfo);

                var averageBytesPerSecond = throughput.AverageBytesPerSecond ?? 0;
                var peakBytesPerSecond = throughput.PeakBytesPerSecond ?? 0;
                var unassembledCount = unassembledCounts.GetValueOrDefault(sourceKey);
                var assembledCount = assembledCounts.GetValueOrDefault(sourceKey);
                var supportedPgns = GetSupportedPgnLists(assembledRecords, address);

                totalAverageBytesPerSecond += averageBytesPerSecond;
                totalPeakBytesPerSecond += peakBytesPerSecond;
                totalUnassembledCount += unassembledCount;
                totalAssembledCount += assembledCount;

                devices.Add(new JsonObject
                {
                    ["address"] = deviceInfo?.Address ?? address,
                    ["productCode"] = deviceInfo?.ProductCode,
                    ["modelId"] = deviceInfo?.ModelID,
                    ["softwareVersionCode"] = deviceInfo?.SoftwareVersionCode,
                    ["modelVersion"] = deviceInfo?.ModelVersion,
                    ["modelSerialCode"] = deviceInfo?.ModelSerialCode,
                    ["manufacturerCode"] = deviceInfo?.MfgCode,
                    ["deviceClass"] = deviceInfo?.DeviceClass,
                    ["deviceFunction"] = deviceInfo?.DeviceFunction,
                    ["unassembledCount"] = unassembledCount,
                    ["assembledCount"] = assembledCount,
                    ["averageBytesPerSecond"] = averageBytesPerSecond,
                    ["peakBytesPerSecond"] = peakBytesPerSecond,
                    ["averageBps"] = FormatBps(throughput.AverageBytesPerSecond),
                    ["peakBps"] = FormatBps(throughput.PeakBytesPerSecond),
                    ["transmitPgns"] = BuildPgnArray(supportedPgns.Transmit),
                    ["receivePgns"] = BuildPgnArray(supportedPgns.Receive)
                });
            }

            return new JsonObject
            {
                ["deviceCount"] = devices.Count,
                ["totalUnassembledCount"] = totalUnassembledCount,
                ["totalAssembledCount"] = totalAssembledCount,
                ["totalAverageBytesPerSecond"] = totalAverageBytesPerSecond,
                ["totalPeakBytesPerSecond"] = totalPeakBytesPerSecond,
                ["totalAverageBps"] = FormatBps(totalAverageBytesPerSecond),
                ["totalPeakBps"] = FormatBps(totalPeakBytesPerSecond),
                ["devices"] = devices
            };
        }

        public static JsonObject ListPgns()
        {
            var records = GetRecords(assembled: true);
            var pgns = new JsonArray(records
                .GroupBy(record => record.PGN)
                .Select(group =>
                {
                    var firstRecord = group.First();
                    var sourceAddresses = group
                        .Select(record => record.Source)
                        .Where(source => !string.IsNullOrWhiteSpace(source))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(source => int.TryParse(source, NumberStyles.Integer, CultureInfo.InvariantCulture, out var address) ? address : int.MaxValue)
                        .ThenBy(source => source, StringComparer.Ordinal)
                        .ToList();

                    return new JsonObject
                    {
                        ["pgn"] = group.Key ?? string.Empty,
                        ["description"] = firstRecord.Description ?? "Unknown",
                        ["sourceAddresses"] = string.Join(", ", sourceAddresses),
                        ["sources"] = new JsonArray(sourceAddresses.Select(source => JsonValue.Create(source)).ToArray()),
                        ["count"] = group.Count()
                    };
                })
                .OrderByDescending(entry => entry["count"]?.GetValue<int>() ?? 0)
                .Select(entry => (JsonNode?)entry)
                .ToArray());

            return new JsonObject
            {
                ["pgnCount"] = pgns.Count,
                ["pgns"] = pgns
            };
        }

        public static JsonObject QueryPackets(JsonObject arguments)
        {
            return QueryPacketSet(arguments);
        }

        public static JsonObject QueryPacketSet(JsonObject arguments)
        {
            ValidateArguments(arguments,
                "assembled",
                "include_pgns",
                "exclude_pgns",
                "src",
                "srcs",
                "dst",
                "dsts",
                "device",
                "limit",
                "offset",
                "include_decoded",
                "only_unknown",
                "only_with_warnings",
                "distinct_data_only");

            var assembled = arguments["assembled"]?.GetValue<bool?>() ?? true;
            var includeDecoded = arguments["include_decoded"]?.GetValue<bool?>() ?? false;
            var onlyUnknown = arguments["only_unknown"]?.GetValue<bool?>() ?? false;
            var onlyWithWarnings = arguments["only_with_warnings"]?.GetValue<bool?>() ?? false;
            var distinctDataOnly = arguments["distinct_data_only"]?.GetValue<bool?>() ?? false;
            var limit = Math.Clamp(arguments["limit"]?.GetValue<int?>() ?? 50, 1, 500);
            var offset = Math.Max(0, arguments["offset"]?.GetValue<int?>() ?? 0);
            var includePgns = ReadStringSet(arguments["include_pgns"]);
            var excludePgns = ReadAddressFilter(arguments["exclude_pgns"]);
            var src = arguments["src"]?.ToString();
            var dst = arguments["dst"]?.ToString();
            var srcs = ReadAddressFilter(arguments["src"], arguments["srcs"]);
            var dsts = ReadAddressFilter(arguments["dst"], arguments["dsts"]);
            var device = arguments["device"]?.ToString();

            var filteredRecords = GetRecords(assembled)
                .Where(record => MatchesAddressFilter(record.PGN, includePgns))
                .Where(record => !MatchesAnyFilter(record.PGN, excludePgns))
                .Where(record => MatchesAddressFilter(record.Source, srcs))
                .Where(record => MatchesAddressFilter(record.Destination, dsts))
                .Where(record => MatchesDevice(record, device))
                .ToList();

            var packets = new JsonArray();
            var distinctData = distinctDataOnly ? new HashSet<string>(StringComparer.Ordinal) : null;
            var matchedCount = 0;

            foreach (var record in filteredRecords)
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

                if (distinctData != null && !distinctData.Add(record.Data ?? string.Empty))
                {
                    continue;
                }

                matchedCount++;
                if (matchedCount <= offset)
                {
                    continue;
                }

                if (packets.Count < limit)
                {
                    packets.Add(BuildPacketJson(record, decoded, includeDecoded));
                }
            }

            return new JsonObject
            {
                ["assembled"] = assembled,
                ["includePgns"] = BuildStringArray(includePgns),
                ["excludePgns"] = BuildStringArray(excludePgns),
                ["src"] = src,
                ["srcs"] = BuildStringArray(srcs),
                ["dst"] = dst,
                ["dsts"] = BuildStringArray(dsts),
                ["device"] = device,
                ["distinctDataOnly"] = distinctDataOnly,
                ["includeDecoded"] = includeDecoded,
                ["onlyUnknown"] = onlyUnknown,
                ["onlyWithWarnings"] = onlyWithWarnings,
                ["limit"] = limit,
                ["offset"] = offset,
                ["preFilterCount"] = filteredRecords.Count,
                ["totalCount"] = matchedCount,
                ["matchedCount"] = matchedCount,
                ["returnedCount"] = packets.Count,
                ["nextOffset"] = offset + packets.Count < matchedCount ? offset + packets.Count : null,
                ["packets"] = packets
            };
        }

        public static JsonObject SearchPayloadHex(JsonObject arguments)
        {
            ValidateArguments(arguments,
                "hex",
                "assembled",
                "include_pgns",
                "exclude_pgns",
                "src",
                "srcs",
                "dst",
                "dsts",
                "device",
                "limit",
                "offset",
                "include_decoded");

            var assembled = arguments["assembled"]?.GetValue<bool?>() ?? true;
            var includeDecoded = arguments["include_decoded"]?.GetValue<bool?>() ?? false;
            var limit = Math.Clamp(arguments["limit"]?.GetValue<int?>() ?? 50, 1, 500);
            var offset = Math.Max(0, arguments["offset"]?.GetValue<int?>() ?? 0);
            var includePgns = ReadStringSet(arguments["include_pgns"]);
            var excludePgns = ReadStringSet(arguments["exclude_pgns"]);
            var src = arguments["src"]?.ToString();
            var dst = arguments["dst"]?.ToString();
            var srcs = ReadAddressFilter(arguments["src"], arguments["srcs"]);
            var dsts = ReadAddressFilter(arguments["dst"], arguments["dsts"]);
            var device = arguments["device"]?.ToString();
            var hex = arguments["hex"]?.ToString();

            if (string.IsNullOrWhiteSpace(hex))
            {
                throw new InvalidOperationException("Missing required argument 'hex'.");
            }

            var pattern = ParseHexSearchPattern(hex);
            var filteredRecords = GetRecords(assembled)
                .Where(record => MatchesAddressFilter(record.PGN, includePgns))
                .Where(record => !MatchesAnyFilter(record.PGN, excludePgns))
                .Where(record => MatchesAddressFilter(record.Source, srcs))
                .Where(record => MatchesAddressFilter(record.Destination, dsts))
                .Where(record => MatchesDevice(record, device))
                .ToList();

            var packets = new JsonArray();
            var matchedCount = 0;
            var totalMatchCount = 0;

            foreach (var record in filteredRecords)
            {
                var matchOffsets = FindPayloadMatches(record.PayloadBytes, pattern).ToList();
                if (matchOffsets.Count == 0)
                {
                    continue;
                }

                matchedCount++;
                totalMatchCount += matchOffsets.Count;
                if (matchedCount <= offset)
                {
                    continue;
                }

                var decoded = includeDecoded ? TryDecodeRecord(record) : null;
                if (packets.Count < limit)
                {
                    var packet = BuildPacketJson(record, decoded, includeDecoded);
                    packet["matchOffsets"] = new JsonArray(matchOffsets.Select(value => JsonValue.Create(value)).ToArray());
                    packet["matchCount"] = matchOffsets.Count;
                    packets.Add(packet);
                }
            }

            return new JsonObject
            {
                ["assembled"] = assembled,
                ["hex"] = hex,
                ["normalizedPattern"] = FormatHexSearchPattern(pattern),
                ["patternLength"] = pattern.Length,
                ["includePgns"] = BuildStringArray(includePgns),
                ["excludePgns"] = BuildStringArray(excludePgns),
                ["src"] = src,
                ["srcs"] = BuildStringArray(srcs),
                ["dst"] = dst,
                ["dsts"] = BuildStringArray(dsts),
                ["device"] = device,
                ["includeDecoded"] = includeDecoded,
                ["limit"] = limit,
                ["offset"] = offset,
                ["preFilterCount"] = filteredRecords.Count,
                ["totalCount"] = matchedCount,
                ["matchedCount"] = matchedCount,
                ["totalPayloadMatchCount"] = totalMatchCount,
                ["returnedCount"] = packets.Count,
                ["nextOffset"] = offset + packets.Count < matchedCount ? offset + packets.Count : null,
                ["packets"] = packets
            };
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

        public static JsonObject GetPacketBySequence(int seq, bool assembled = true, bool includeDecoded = true)
        {
            var record = GetRecords(assembled).FirstOrDefault(item => item.LogSequenceNumber == seq)
                ?? throw new InvalidOperationException($"Packet with seq {seq} was not found.");

            return BuildPacketJson(record, includeDecoded ? TryDecodeRecord(record) : null, includeDecoded);
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

        public static JsonObject GetAppliedPgnDefinition(JsonObject arguments)
        {
            var assembled = arguments["assembled"]?.GetValue<bool?>() ?? true;
            var seq = arguments["seq"]?.GetValue<int?>()
                ?? throw new InvalidOperationException("Missing required argument 'seq'.");

            var record = GetRecords(assembled).FirstOrDefault(item => item.LogSequenceNumber == seq)
                ?? throw new InvalidOperationException($"Packet with seq {seq} was not found.");

            if (!int.TryParse(record.PGN, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pgn))
            {
                throw new InvalidOperationException($"PGN '{record.PGN}' is not a valid integer.");
            }

            var currentDefinition = ResolveDefinitionForRecord(record);
            var editableDefinition = PgnDefinitions.PrepareEditablePgnDefinition(pgn, currentDefinition, record.PayloadBytes);

            return new JsonObject
            {
                ["seq"] = seq,
                ["assembled"] = assembled,
                ["pgn"] = pgn,
                ["existsInLocal"] = editableDefinition.ExistsInLocal,
                ["existsInCanboat"] = editableDefinition.ExistsInCanboat,
                ["definition"] = JsonNode.Parse(editableDefinition.Json),
                ["matchSuggestion"] = BuildMatchSuggestionJson(record)
            };
        }

        public static async Task<JsonObject> SetAppliedPgnDefinitionAsync(JsonObject arguments, CancellationToken cancellationToken = default)
        {
            var assembled = arguments["assembled"]?.GetValue<bool?>() ?? true;
            var seq = arguments["seq"]?.GetValue<int?>()
                ?? throw new InvalidOperationException("Missing required argument 'seq'.");

            var definitionJson = arguments["definition_json"]?.ToString();
            if (string.IsNullOrWhiteSpace(definitionJson) && arguments["definition"] != null)
            {
                definitionJson = arguments["definition"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
            }

            if (string.IsNullOrWhiteSpace(definitionJson))
            {
                throw new InvalidOperationException("Missing required argument 'definition_json' or 'definition'.");
            }

            var record = GetRecords(assembled).FirstOrDefault(item => item.LogSequenceNumber == seq)
                ?? throw new InvalidOperationException($"Packet with seq {seq} was not found.");

            if (!int.TryParse(record.PGN, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pgn))
            {
                throw new InvalidOperationException($"PGN '{record.PGN}' is not a valid integer.");
            }

            var currentDefinition = ResolveDefinitionForRecord(record);
            PgnDefinitions.SaveEditablePgnDefinition(pgn, definitionJson, currentDefinition, record.PayloadBytes);
            await ReloadDefinitionsAsync(cancellationToken);
            return GetAppliedPgnDefinition(new JsonObject
            {
                ["seq"] = seq,
                ["assembled"] = assembled
            });
        }

        public static async Task<JsonObject> ClearAppliedPgnDefinitionAsync(JsonObject arguments, CancellationToken cancellationToken = default)
        {
            var assembled = arguments["assembled"]?.GetValue<bool?>() ?? true;
            var seq = arguments["seq"]?.GetValue<int?>()
                ?? throw new InvalidOperationException("Missing required argument 'seq'.");

            var record = GetRecords(assembled).FirstOrDefault(item => item.LogSequenceNumber == seq)
                ?? throw new InvalidOperationException($"Packet with seq {seq} was not found.");

            if (!int.TryParse(record.PGN, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pgn))
            {
                throw new InvalidOperationException($"PGN '{record.PGN}' is not a valid integer.");
            }

            var currentDefinition = ResolveDefinitionForRecord(record);
            var removed = PgnDefinitions.RemoveEditablePgnDefinition(pgn, currentDefinition, record.PayloadBytes);
            if (removed)
            {
                await ReloadDefinitionsAsync(cancellationToken);
            }

            return new JsonObject
            {
                ["seq"] = seq,
                ["assembled"] = assembled,
                ["pgn"] = pgn,
                ["removed"] = removed
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

        public static async Task<JsonObject> GetFilterStateAsync(CancellationToken cancellationToken = default)
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

                    return mainWindow.GetFilterStateForMcp();
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

        private static (IReadOnlyList<int> Transmit, IReadOnlyList<int> Receive) GetSupportedPgnLists(
            IReadOnlyList<MainWindow.Nmea2000Record> assembledRecords,
            int address)
        {
            var transmit = new SortedSet<int>();
            var receive = new SortedSet<int>();
            var sourceText = address.ToString(CultureInfo.InvariantCulture);

            foreach (var record in assembledRecords)
            {
                if (!string.Equals(record.Source, sourceText, StringComparison.Ordinal) ||
                    !string.Equals(record.PGN, "126464", StringComparison.Ordinal))
                {
                    continue;
                }

                PopulateSupportedPgnSet(record.PayloadBytes, transmit, receive);
            }

            return (transmit.ToList(), receive.ToList());
        }

        private static void PopulateSupportedPgnSet(byte[] payloadBytes, ISet<int> transmitPgns, ISet<int> receivePgns)
        {
            if (payloadBytes == null || payloadBytes.Length < 4)
            {
                return;
            }

            var targetSet = payloadBytes[0] switch
            {
                0 => transmitPgns,
                1 => receivePgns,
                _ => null
            };

            if (targetSet == null)
            {
                return;
            }

            for (var offset = 1; offset + 2 < payloadBytes.Length; offset += 3)
            {
                var pgn = payloadBytes[offset]
                          | (payloadBytes[offset + 1] << 8)
                          | (payloadBytes[offset + 2] << 16);
                targetSet.Add(pgn);
            }
        }

        private static JsonArray BuildPgnArray(IEnumerable<int> pgns)
        {
            return new JsonArray(pgns
                .Select(pgn => new JsonObject
                {
                    ["pgn"] = pgn,
                    ["description"] = GetPgnDescription(pgn),
                    ["display"] = FormatSupportedPgnEntry(pgn)
                })
                .Select(item => (JsonNode?)item)
                .ToArray());
        }

        private static string FormatSupportedPgnEntry(int pgn)
        {
            var description = GetPgnDescription(pgn);
            return string.IsNullOrWhiteSpace(description)
                ? pgn.ToString(CultureInfo.InvariantCulture)
                : $"{pgn} - {description}";
        }

        private static string GetPgnDescription(int pgn)
        {
            var canboatRoot = (Application.Current as App)?.CanboatRoot ?? Globals.CanboatRoot;
            if (canboatRoot?.PGNs == null)
            {
                return string.Empty;
            }

            var descriptions = canboatRoot.PGNs
                .Where(definition => definition.PGN == pgn && !string.IsNullOrWhiteSpace(definition.Description))
                .Select(definition => definition.Description.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return descriptions.Count == 1
                ? descriptions[0]
                : string.Empty;
        }

        private static (double? AverageBytesPerSecond, double? PeakBytesPerSecond) CalculateThroughput(
            IEnumerable<MainWindow.Nmea2000Record> records)
        {
            var timestampedRecords = records
                .Select(record => new
                {
                    Timestamp = ParseTimestamp(record.Timestamp),
                    PayloadLength = record.PayloadBytes.Length
                })
                .Where(item => item.Timestamp.HasValue)
                .Select(item => new
                {
                    Timestamp = item.Timestamp!.Value,
                    item.PayloadLength
                })
                .ToList();

            if (timestampedRecords.Count == 0)
            {
                return (null, null);
            }

            var totalBytes = timestampedRecords.Sum(item => item.PayloadLength);
            var firstTimestamp = timestampedRecords.Min(item => item.Timestamp);
            var lastTimestamp = timestampedRecords.Max(item => item.Timestamp);
            var durationSeconds = (lastTimestamp - firstTimestamp).TotalSeconds;
            double? averageBytesPerSecond = durationSeconds > 0 ? totalBytes / durationSeconds : null;

            var peakBytesPerSecond = timestampedRecords
                .GroupBy(item => item.Timestamp.ToUnixTimeSeconds())
                .Select(group => (double)group.Sum(item => item.PayloadLength))
                .DefaultIfEmpty(0)
                .Max();

            return (averageBytesPerSecond, peakBytesPerSecond > 0 ? peakBytesPerSecond : null);
        }

        private static string FormatBps(double? bytesPerSecond)
        {
            return bytesPerSecond.HasValue
                ? bytesPerSecond.Value.ToString("0.##", CultureInfo.InvariantCulture)
                : string.Empty;
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

        private static bool MatchesDevice(MainWindow.Nmea2000Record record, string? device)
        {
            if (string.IsNullOrWhiteSpace(device))
            {
                return true;
            }

            return string.Equals(record.Source, device, StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(record.DeviceInfo)
                    && record.DeviceInfo.Contains(device, StringComparison.OrdinalIgnoreCase));
        }

        private static void ValidateArguments(JsonObject arguments, params string[] allowedNames)
        {
            var allowed = new HashSet<string>(allowedNames, StringComparer.Ordinal);
            var unknown = arguments
                .Select(item => item.Key)
                .Where(name => !allowed.Contains(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            if (unknown.Count > 0)
            {
                throw new ArgumentException($"InputValidationError: Unknown argument(s): {string.Join(", ", unknown)}.");
            }
        }

        private static IReadOnlySet<string> ReadAddressFilter(params JsonNode?[] nodes)
        {
            return ReadStringSet(nodes);
        }

        private static IReadOnlySet<string> ReadStringSet(params JsonNode?[] nodes)
        {
            var values = nodes
                .SelectMany(ReadStringArray)
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value));

            return new HashSet<string>(values, StringComparer.Ordinal);
        }

        private static bool MatchesAddressFilter(string? address, IReadOnlySet<string> allowedAddresses)
        {
            return allowedAddresses.Count == 0
                || (!string.IsNullOrWhiteSpace(address) && allowedAddresses.Contains(address));
        }

        private static bool MatchesAnyFilter(string? value, IReadOnlySet<string> filteredValues)
        {
            return filteredValues.Count > 0
                && !string.IsNullOrWhiteSpace(value)
                && filteredValues.Contains(value);
        }

        private static JsonArray BuildStringArray(IEnumerable<string> values)
        {
            return new JsonArray(values
                .OrderBy(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var address) ? address : int.MaxValue)
                .ThenBy(value => value, StringComparer.Ordinal)
                .Select(value => JsonValue.Create(value))
                .ToArray());
        }

        private static byte?[] ParseHexSearchPattern(string hex)
        {
            var tokens = TokenizeHexPattern(hex);
            if (tokens.Count == 0)
            {
                throw new InvalidOperationException("Hex search pattern does not contain any bytes.");
            }

            var pattern = new byte?[tokens.Count];
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token == "??")
                {
                    pattern[i] = null;
                    continue;
                }

                if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                {
                    throw new InvalidOperationException($"Invalid hex byte '{token}'.");
                }

                pattern[i] = value;
            }

            return pattern;
        }

        private static List<string> TokenizeHexPattern(string hex)
        {
            var normalized = hex
                .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(",", " ", StringComparison.Ordinal)
                .Replace(":", " ", StringComparison.Ordinal)
                .Replace("-", " ", StringComparison.Ordinal);

            var separatedTokens = normalized
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (separatedTokens.Length > 1)
            {
                return separatedTokens.Select(NormalizeHexToken).ToList();
            }

            var compact = separatedTokens.Length == 1 ? separatedTokens[0] : string.Empty;
            if (compact.Length == 0)
            {
                return new List<string>();
            }

            if (compact.Length % 2 != 0)
            {
                throw new InvalidOperationException("Compact hex search patterns must have an even number of hex characters.");
            }

            var tokens = new List<string>(compact.Length / 2);
            for (var i = 0; i < compact.Length; i += 2)
            {
                tokens.Add(NormalizeHexToken(compact.Substring(i, 2)));
            }

            return tokens;
        }

        private static string NormalizeHexToken(string token)
        {
            var normalized = token.Trim();
            if (normalized == "?")
            {
                return "??";
            }

            if (normalized == "??")
            {
                return normalized;
            }

            if (normalized.Length == 1)
            {
                normalized = "0" + normalized;
            }

            if (normalized.Length != 2 || normalized.Any(value => !Uri.IsHexDigit(value)))
            {
                throw new InvalidOperationException($"Invalid hex byte '{token}'.");
            }

            return normalized.ToUpperInvariant();
        }

        private static IEnumerable<int> FindPayloadMatches(byte[] payloadBytes, byte?[] pattern)
        {
            if (payloadBytes == null || pattern.Length == 0 || payloadBytes.Length < pattern.Length)
            {
                yield break;
            }

            for (var offset = 0; offset <= payloadBytes.Length - pattern.Length; offset++)
            {
                var matched = true;
                for (var patternIndex = 0; patternIndex < pattern.Length; patternIndex++)
                {
                    var expected = pattern[patternIndex];
                    if (expected.HasValue && payloadBytes[offset + patternIndex] != expected.Value)
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    yield return offset;
                }
            }
        }

        private static string FormatHexSearchPattern(byte?[] pattern)
        {
            return string.Join(" ", pattern.Select(value => value.HasValue ? $"0x{value.Value:X2}" : "??"));
        }

        private static Canboat.Pgn? ResolveDefinitionForRecord(MainWindow.Nmea2000Record record)
        {
            var app = Application.Current as App;
            var canboatRoot = app?.CanboatRoot;
            if (canboatRoot == null)
            {
                return null;
            }

            if (record.PGNListIndex.HasValue &&
                record.PGNListIndex.Value >= 0 &&
                record.PGNListIndex.Value < canboatRoot.PGNs.Count)
            {
                return canboatRoot.PGNs[record.PGNListIndex.Value];
            }

            return int.TryParse(record.PGN, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pgn)
                ? canboatRoot.PGNs.FirstOrDefault(candidate => candidate.PGN == pgn)
                : null;
        }

        private static JsonObject? BuildMatchSuggestionJson(MainWindow.Nmea2000Record record)
        {
            if (record.PayloadBytes == null || record.PayloadBytes.Length < 2)
            {
                return null;
            }

            var header = (ushort)(record.PayloadBytes[0] | (record.PayloadBytes[1] << 8));
            var manufacturerCode = header & 0x07FF;
            var industryCode = (header >> 13) & 0x0007;

            return new JsonObject
            {
                ["manufacturerCode"] = manufacturerCode,
                ["manufacturerDescription"] = PgnDefinitions.Lookup("MANUFACTURER_CODE", manufacturerCode),
                ["industryCode"] = industryCode,
                ["industryDescription"] = PgnDefinitions.Lookup("INDUSTRY_CODE", industryCode)
            };
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
                if (Seatalk1Parser.TryDecodeEmbeddedSeatalk1(record.PGN, record.PayloadBytes) is { } seatalk1Decode)
                {
                    return seatalk1Decode;
                }

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

            if (double.TryParse(timestamp, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                var wholeSeconds = Math.Truncate(seconds);
                var fractionalSeconds = seconds - wholeSeconds;
                return DateTimeOffset.UnixEpoch
                    .AddSeconds(wholeSeconds)
                    .AddTicks((long)Math.Round(fractionalSeconds * TimeSpan.TicksPerSecond));
            }

            return DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : TimeSpan.TryParse(timestamp, CultureInfo.InvariantCulture, out var timeSpan)
                    ? DateTimeOffset.UnixEpoch.Add(timeSpan)
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
