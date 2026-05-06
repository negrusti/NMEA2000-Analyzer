using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using static NMEA2000Analyzer.MainWindow;

namespace NMEA2000Analyzer
{
    internal static class DeviceEmulatorScaffoldGenerator
    {
        private static readonly HashSet<uint> IdentityPgns = new()
        {
            59904,
            60928,
            126464,
            126996,
            126998
        };

        public sealed class Result
        {
            public required string ProjectDirectory { get; init; }
            public required string ProjectName { get; init; }
            public required int ReceivedPgnCount { get; init; }
            public required int TransmittedPgnCount { get; init; }
        }

        private sealed class ObservedPgnSummary
        {
            public required uint Pgn { get; init; }
            public required string Description { get; init; }
            public required int Count { get; init; }
            public required byte Priority { get; init; }
            public required bool IsFastPacket { get; init; }
            public required string SamplePayloadHex { get; init; }
            public required string SampleAscii { get; init; }
            public required byte SamplePeerAddress { get; init; }
            public required byte[] SamplePayloadBytes { get; init; }
            public double? AverageIntervalMs { get; init; }
            public List<byte> PeerAddresses { get; init; } = new();
        }

        private sealed class ObservedTrafficManifest
        {
            public required string DeviceName { get; init; }
            public required byte SourceAddress { get; init; }
            public required List<object> TransmittedPgns { get; init; }
            public required List<object> ReceivedPgns { get; init; }
        }

        public static Result Generate(
            DeviceStatisticsEntry entry,
            IReadOnlyList<Nmea2000Record>? rawRecords,
            IReadOnlyList<Nmea2000Record>? assembledRecords,
            string selectedDirectory)
        {
            if (rawRecords == null || rawRecords.Count == 0 || assembledRecords == null || assembledRecords.Count == 0)
            {
                throw new InvalidOperationException("No log data is loaded.");
            }

            if (!Directory.Exists(selectedDirectory))
            {
                throw new DirectoryNotFoundException($"Target directory does not exist: {selectedDirectory}");
            }

            var sourceText = entry.Address.ToString(CultureInfo.InvariantCulture);
            var transmittedRecords = assembledRecords
                .Where(record => string.Equals(record.Source, sourceText, StringComparison.Ordinal))
                .OrderBy(record => record.LogSequenceNumber)
                .ToList();
            var receivedRecords = assembledRecords
                .Where(record =>
                    !string.Equals(record.Source, sourceText, StringComparison.Ordinal) &&
                    (string.Equals(record.Destination, sourceText, StringComparison.Ordinal) ||
                     (string.Equals(record.PGN, "59904", StringComparison.Ordinal) && string.Equals(record.Destination, "255", StringComparison.Ordinal))))
                .OrderBy(record => record.LogSequenceNumber)
                .ToList();
            var relatedRawRecords = rawRecords
                .Where(record =>
                    string.Equals(record.Source, sourceText, StringComparison.Ordinal) ||
                    string.Equals(record.Destination, sourceText, StringComparison.Ordinal) ||
                    (string.Equals(record.PGN, "59904", StringComparison.Ordinal) && string.Equals(record.Destination, "255", StringComparison.Ordinal)))
                .OrderBy(record => record.LogSequenceNumber)
                .ToList();
            var relatedAssembledRecords = assembledRecords
                .Where(record =>
                    string.Equals(record.Source, sourceText, StringComparison.Ordinal) ||
                    string.Equals(record.Destination, sourceText, StringComparison.Ordinal) ||
                    (string.Equals(record.PGN, "59904", StringComparison.Ordinal) && string.Equals(record.Destination, "255", StringComparison.Ordinal)))
                .OrderBy(record => record.LogSequenceNumber)
                .ToList();

            if (transmittedRecords.Count == 0 && receivedRecords.Count == 0 && relatedRawRecords.Count == 0)
            {
                throw new InvalidOperationException("No device-specific traffic was found in the loaded log.");
            }

            var projectName = BuildProjectName(entry);
            var displayName = BuildDisplayName(projectName);
            var projectDirectory = selectedDirectory;
            Directory.CreateDirectory(projectDirectory);
            var logsDirectory = Path.Combine(projectDirectory, "logs");
            Directory.CreateDirectory(logsDirectory);

            var transmittedSummaries = BuildSummaries(transmittedRecords, record => ParseByte(record.Destination, 255));
            var receivedSummaries = BuildSummaries(receivedRecords, record => ParseByte(record.Source, 255));
            var emulatorClassName = $"{SanitizeIdentifier(projectName)}DeviceEmulator";
            var namespaceName = SanitizeIdentifier(projectName);
            var defaultSourceAddress = (byte)entry.Address;
            var defaultAddressClaimPayload = transmittedSummaries.FirstOrDefault(summary => summary.Pgn == 60928)?.SamplePayloadBytes ?? Array.Empty<byte>();

            WriteFile(projectDirectory, $"{projectName}.csproj", BuildProjectFile(projectName));
            WriteFile(projectDirectory, "Program.cs", BuildProgramFile(namespaceName, emulatorClassName, displayName));
            WriteFile(projectDirectory, "build.cmd", BuildBuildCommandFile(projectName));
            WriteFile(projectDirectory, "EmulatorConfig.cs", BuildEmulatorConfigFile(namespaceName, defaultSourceAddress));
            WriteFile(projectDirectory, "PcanBus.cs", BuildPcanBusFile(namespaceName));
            WriteFile(projectDirectory, "Nmea2000Protocol.cs", BuildProtocolFile(namespaceName));
            WriteFile(projectDirectory, "DeviceEmulatorBase.cs", BuildBaseEmulatorFile(namespaceName, defaultAddressClaimPayload));
            WriteFile(projectDirectory, $"{emulatorClassName}.cs", BuildGeneratedEmulatorFile(namespaceName, emulatorClassName, entry, transmittedSummaries, receivedSummaries));
            WriteFile(projectDirectory, "appsettings.json", BuildAppSettings(defaultSourceAddress));
            WriteFile(projectDirectory, "ObservedTraffic.json", BuildObservedTrafficJson(entry, transmittedSummaries, receivedSummaries));
            WriteFile(projectDirectory, "README.md", BuildReadme(projectName, emulatorClassName, entry, transmittedSummaries, receivedSummaries));
            WriteFile(projectDirectory, "AGENTS.md", BuildAgentsFile(emulatorClassName));
            WriteFile(logsDirectory, "raw-device-records.json", BuildRecordsJson(relatedRawRecords));
            WriteFile(logsDirectory, "assembled-device-records.json", BuildRecordsJson(relatedAssembledRecords));

            return new Result
            {
                ProjectDirectory = projectDirectory,
                ProjectName = projectName,
                ReceivedPgnCount = receivedSummaries.Count,
                TransmittedPgnCount = transmittedSummaries.Count
            };
        }

        private static List<ObservedPgnSummary> BuildSummaries(
            IReadOnlyList<Nmea2000Record> records,
            Func<Nmea2000Record, byte> peerSelector)
        {
            return records
                .GroupBy(record => record.PGN, StringComparer.Ordinal)
                .Select(group =>
                {
                    var first = group.First();
                    var pgn = uint.Parse(first.PGN, CultureInfo.InvariantCulture);
                    var timestamps = group
                        .Select(TryParseTimestamp)
                        .Where(timestamp => timestamp.HasValue)
                        .Select(timestamp => timestamp!.Value)
                        .OrderBy(timestamp => timestamp)
                        .ToList();

                    double? averageIntervalMs = null;
                    if (timestamps.Count >= 2)
                    {
                        var intervals = new List<double>();
                        for (var i = 1; i < timestamps.Count; i++)
                        {
                            intervals.Add((timestamps[i] - timestamps[i - 1]).TotalMilliseconds);
                        }

                        if (intervals.Count > 0)
                        {
                            averageIntervalMs = intervals.Average();
                        }
                    }

                    return new ObservedPgnSummary
                    {
                        Pgn = pgn,
                        Description = GetPgnDescription(first.PGN),
                        Count = group.Count(),
                        Priority = ParseByte(first.Priority, 3),
                        IsFastPacket = IsFastPacket(first),
                        SamplePayloadHex = FormatPayloadHex(first.PayloadBytes),
                        SampleAscii = first.AsciiData,
                        SamplePeerAddress = peerSelector(first),
                        SamplePayloadBytes = first.PayloadBytes.ToArray(),
                        AverageIntervalMs = averageIntervalMs,
                        PeerAddresses = group.Select(peerSelector).Distinct().OrderBy(address => address).ToList()
                    };
                })
                .OrderBy(summary => summary.Pgn)
                .ToList();
        }

        private static DateTimeOffset? TryParseTimestamp(Nmea2000Record record)
        {
            if (string.IsNullOrWhiteSpace(record.Timestamp))
            {
                return null;
            }

            if (double.TryParse(record.Timestamp, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return DateTimeOffset.UnixEpoch.AddSeconds(seconds);
            }

            if (DateTimeOffset.TryParse(record.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            {
                return timestamp;
            }

            if (TimeSpan.TryParse(record.Timestamp, CultureInfo.InvariantCulture, out var timeSpan))
            {
                return DateTimeOffset.UnixEpoch.Add(timeSpan);
            }

            return null;
        }

        private static bool IsFastPacket(Nmea2000Record record)
        {
            if (record.PayloadBytes.Length > 8)
            {
                return true;
            }

            return int.TryParse(record.PGN, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pgn) &&
                Globals.UniquePGNs != null &&
                Globals.UniquePGNs.TryGetValue(pgn, out var definition) &&
                string.Equals(definition.Type, "Fast", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetPgnDescription(string pgnText)
        {
            if (int.TryParse(pgnText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pgn) &&
                Globals.UniquePGNs != null &&
                Globals.UniquePGNs.TryGetValue(pgn, out var definition) &&
                !string.IsNullOrWhiteSpace(definition.Description))
            {
                return definition.Description.Trim();
            }

            return "Unknown PGN";
        }

        private static string BuildProjectName(DeviceStatisticsEntry entry)
        {
            var baseName = JoinNonEmpty(" ", entry.MfgCode ?? string.Empty, entry.ModelID ?? string.Empty, "Emulator");
            return SanitizeIdentifier(string.IsNullOrWhiteSpace(baseName) ? $"Device{entry.Address}Emulator" : baseName);
        }

        private static string BuildDisplayName(string projectName)
        {
            const string suffix = "DeviceEmulator";
            return projectName.EndsWith(suffix, StringComparison.Ordinal)
                ? projectName[..^suffix.Length]
                : projectName;
        }

        private static string BuildProjectFile(string projectName)
        {
            return RenderTemplate("Project.csproj.tpl", new Dictionary<string, string>
            {
                ["ProjectName"] = projectName
            });
        }

        private static string BuildProgramFile(string namespaceName, string emulatorClassName, string displayName)
        {
            return RenderTemplate("Program.cs.tpl", new Dictionary<string, string>
            {
                ["NamespaceName"] = namespaceName,
                ["EmulatorClassName"] = emulatorClassName,
                ["DisplayName"] = displayName
            });
        }

        private static string BuildBuildCommandFile(string projectName)
        {
            return RenderTemplate("build.cmd.tpl", new Dictionary<string, string>
            {
                ["ProjectName"] = projectName
            });
        }

        private static string BuildEmulatorConfigFile(string namespaceName, byte sourceAddress)
        {
            return RenderTemplate("EmulatorConfig.cs.tpl", new Dictionary<string, string>
            {
                ["NamespaceName"] = namespaceName,
                ["SourceAddress"] = sourceAddress.ToString(CultureInfo.InvariantCulture)
            });
        }

        private static string BuildPcanBusFile(string namespaceName)
        {
            return RenderTemplate("PcanBus.cs.tpl", new Dictionary<string, string>
            {
                ["NamespaceName"] = namespaceName
            });
        }

        private static string BuildProtocolFile(string namespaceName)
        {
            return RenderTemplate("Nmea2000Protocol.cs.tpl", new Dictionary<string, string>
            {
                ["NamespaceName"] = namespaceName
            });
        }

        private static string BuildBaseEmulatorFile(string namespaceName, byte[] addressClaimPayload)
        {
            return RenderTemplate("DeviceEmulatorBase.cs.tpl", new Dictionary<string, string>
            {
                ["NamespaceName"] = namespaceName,
                ["DefaultAddressClaimLiteral"] = FormatByteArrayLiteral(addressClaimPayload)
            });
        }

        private static string BuildGeneratedEmulatorFile(
            string namespaceName,
            string emulatorClassName,
            DeviceStatisticsEntry entry,
            IReadOnlyList<ObservedPgnSummary> transmittedSummaries,
            IReadOnlyList<ObservedPgnSummary> receivedSummaries)
        {
            var fastPacketPgns = transmittedSummaries
                .Concat(receivedSummaries)
                .Where(summary => summary.IsFastPacket)
                .Select(summary => summary.Pgn)
                .Distinct()
                .OrderBy(pgn => pgn)
                .ToList();
            var periodicSuggestions = transmittedSummaries
                .Where(summary => summary.AverageIntervalMs.HasValue && summary.Count >= 3 && !IdentityPgns.Contains(summary.Pgn))
                .OrderBy(summary => summary.Pgn)
                .ToList();

            var builder = new StringBuilder();
            builder.AppendLine($"namespace {namespaceName};");
            builder.AppendLine();
            builder.AppendLine($"public sealed class {emulatorClassName} : DeviceEmulatorBase");
            builder.AppendLine("{");
            builder.AppendLine($"    public {emulatorClassName}(PcanBus bus, EmulatorConfig config) : base(bus, config)");
            builder.AppendLine("    {");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    protected override void ConfigurePeriodicMessages()");
            builder.AppendLine("    {");
            if (periodicSuggestions.Count == 0)
            {
                builder.AppendLine("        // No obvious periodic PGNs were detected from the capture.");
                builder.AppendLine("        // RegisterPeriodicMessage(\"example\", TimeSpan.FromSeconds(1), ct => SendPgn127250Async(0xFF, ct));");
            }
            else
            {
                builder.AppendLine("        // Uncomment and adjust the schedules below once you are ready to replay periodic traffic.");
                foreach (var summary in periodicSuggestions)
                {
                    builder.AppendLine($"        // Observed PGN {summary.Pgn} ({EscapeComment(summary.Description)}) {summary.Count} times.");
                    builder.AppendLine($"        // RegisterPeriodicMessage(\"PGN {summary.Pgn}\", TimeSpan.FromMilliseconds({summary.AverageIntervalMs!.Value.ToString("0.###", CultureInfo.InvariantCulture)}), ct => SendPgn{summary.Pgn}Async({FormatByte(summary.SamplePeerAddress)}, ct));");
                }
            }

            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    protected override bool IsFastPacketPgn(uint pgn)");
            builder.AppendLine("    {");
            if (fastPacketPgns.Count == 0)
            {
                builder.AppendLine("        return false;");
            }
            else
            {
                builder.AppendLine("        return pgn switch");
                builder.AppendLine("        {");
                foreach (var pgn in fastPacketPgns)
                {
                    builder.AppendLine($"            {pgn} => true,");
                }

                builder.AppendLine("            _ => false");
                builder.AppendLine("        };");
            }

            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    protected override async Task HandleMessageAsync(Nmea2000Message message, CancellationToken cancellationToken)");
            builder.AppendLine("    {");
            builder.AppendLine("        switch (message.Pgn)");
            builder.AppendLine("        {");
            foreach (var summary in receivedSummaries)
            {
                builder.AppendLine($"            case {summary.Pgn}:");
                builder.AppendLine($"                await OnPgn{summary.Pgn}ReceivedAsync(message, cancellationToken).ConfigureAwait(false);");
                builder.AppendLine("                break;");
            }

            builder.AppendLine("            default:");
            builder.AppendLine("                break;");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine();
            foreach (var summary in receivedSummaries)
            {
                builder.AppendLine($"    private async Task OnPgn{summary.Pgn}ReceivedAsync(Nmea2000Message message, CancellationToken cancellationToken)");
                builder.AppendLine("    {");
                builder.AppendLine($"        // {EscapeComment(summary.Description)}");
                builder.AppendLine($"        // Observed {summary.Count} time(s) from source(s): {FormatAddressList(summary.PeerAddresses)}.");
                builder.AppendLine($"        // Sample payload: {summary.SamplePayloadHex}");
                if (summary.Pgn == 59904)
                {
                    builder.AppendLine("        if (TryParseIsoRequest(message.Payload, out var requestedPgn))");
                    builder.AppendLine("        {");
                    builder.AppendLine("            switch (requestedPgn)");
                    builder.AppendLine("            {");
                    foreach (var responseSummary in transmittedSummaries.Where(item => IdentityPgns.Contains(item.Pgn)))
                    {
                        builder.AppendLine($"                case {responseSummary.Pgn}:");
                        builder.AppendLine($"                    await SendPgn{responseSummary.Pgn}Async(message.SourceAddress, cancellationToken).ConfigureAwait(false);");
                        builder.AppendLine("                    return;");
                    }

                    builder.AppendLine("                default:");
                    builder.AppendLine("                    break;");
                    builder.AppendLine("            }");
                    builder.AppendLine("        }");
                    builder.AppendLine();
                    builder.AppendLine("        await Task.CompletedTask;");
                }
                else
                {
                    builder.AppendLine("        // TODO: decode fields and implement the required reaction for this incoming PGN.");
                    builder.AppendLine("        await Task.CompletedTask;");
                }

                builder.AppendLine("    }");
                builder.AppendLine();
            }

            foreach (var summary in transmittedSummaries)
            {
                builder.AppendLine($"    public Task SendPgn{summary.Pgn}Async(byte destinationAddress, CancellationToken cancellationToken)");
                builder.AppendLine("    {");
                builder.AppendLine($"        // {EscapeComment(summary.Description)}");
                builder.AppendLine($"        // Observed {summary.Count} time(s) to destination(s): {FormatAddressList(summary.PeerAddresses)}.");
                if (summary.AverageIntervalMs.HasValue)
                {
                    builder.AppendLine($"        // Average observed interval: {summary.AverageIntervalMs.Value.ToString("0.###", CultureInfo.InvariantCulture)} ms.");
                }

                builder.AppendLine($"        return SendMessageAsync({summary.Pgn}, {summary.Priority}, destinationAddress, BuildPgn{summary.Pgn}Payload(), cancellationToken);");
                builder.AppendLine("    }");
                builder.AppendLine();
                builder.AppendLine($"    private byte[] BuildPgn{summary.Pgn}Payload()");
                builder.AppendLine("    {");
                builder.AppendLine($"        // Sample payload from the capture. Replace this with a typed serializer when you implement PGN {summary.Pgn}.");
                builder.AppendLine($"        return {FormatByteArrayLiteral(summary.SamplePayloadBytes)};");
                builder.AppendLine("    }");
                builder.AppendLine();
            }

            builder.AppendLine("}");
            return builder.ToString();
        }

        private static string BuildAppSettings(byte sourceAddress)
        {
            return $$"""
                {
                  "Channel": "Usb01",
                  "Bitrate": "Pcan250",
                  "SourceAddress": {{sourceAddress}}
                }
                """;
        }

        private static string BuildObservedTrafficJson(
            DeviceStatisticsEntry entry,
            IReadOnlyList<ObservedPgnSummary> transmittedSummaries,
            IReadOnlyList<ObservedPgnSummary> receivedSummaries)
        {
            var manifest = new ObservedTrafficManifest
            {
                DeviceName = JoinNonEmpty(" ", entry.MfgCode ?? string.Empty, entry.ModelID ?? string.Empty),
                SourceAddress = (byte)entry.Address,
                TransmittedPgns = transmittedSummaries.Select(ToManifestItem).ToList(),
                ReceivedPgns = receivedSummaries.Select(ToManifestItem).ToList()
            };

            return JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }

        private static string BuildRecordsJson(IReadOnlyList<Nmea2000Record> records)
        {
            var payload = records.Select(record => new
            {
                record.LogSequenceNumber,
                record.Timestamp,
                record.Source,
                record.Destination,
                record.PGN,
                record.Type,
                record.Priority,
                record.Description,
                PayloadHex = FormatPayloadHex(record.PayloadBytes),
                record.AsciiData
            });

            return JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }

        private static string BuildReadme(
            string projectName,
            string emulatorClassName,
            DeviceStatisticsEntry entry,
            IReadOnlyList<ObservedPgnSummary> transmittedSummaries,
            IReadOnlyList<ObservedPgnSummary> receivedSummaries)
        {
            var deviceName = JoinNonEmpty(" ", entry.MfgCode ?? string.Empty, entry.ModelID ?? string.Empty);
            return RenderTemplate("README.md.tpl", new Dictionary<string, string>
            {
                ["ProjectName"] = projectName,
                ["DeviceName"] = deviceName,
                ["SourceAddress"] = entry.Address.ToString(CultureInfo.InvariantCulture),
                ["EmulatorClassFileName"] = $"{emulatorClassName}.cs",
                ["TransmittedPgnCount"] = transmittedSummaries.Count.ToString(CultureInfo.InvariantCulture),
                ["ReceivedPgnCount"] = receivedSummaries.Count.ToString(CultureInfo.InvariantCulture)
            });
        }

        private static string BuildAgentsFile(string emulatorClassName)
        {
            return RenderTemplate("AGENTS.md.tpl", new Dictionary<string, string>
            {
                ["EmulatorClassFileName"] = $"{emulatorClassName}.cs"
            });
        }

        private static object ToManifestItem(ObservedPgnSummary summary)
        {
            return new
            {
                summary.Pgn,
                summary.Description,
                summary.Count,
                summary.Priority,
                summary.IsFastPacket,
                summary.SamplePayloadHex,
                summary.SampleAscii,
                summary.SamplePeerAddress,
                summary.AverageIntervalMs,
                PeerAddresses = summary.PeerAddresses
            };
        }

        private static string RenderTemplate(string templateFileName, IReadOnlyDictionary<string, string> values)
        {
            var templatePath = Path.Combine(AppContext.BaseDirectory, "ScaffoldTemplates", templateFileName);
            if (!File.Exists(templatePath))
            {
                throw new FileNotFoundException($"Missing scaffold template: {templatePath}");
            }

            var template = File.ReadAllText(templatePath);
            foreach (var pair in values)
            {
                template = template.Replace($"{{{{{pair.Key}}}}}", pair.Value, StringComparison.Ordinal);
            }

            return template;
        }

        private static void WriteFile(string directory, string fileName, string content)
        {
            File.WriteAllText(Path.Combine(directory, fileName), content, Encoding.UTF8);
        }

        private static string FormatPayloadHex(byte[] payload)
        {
            return string.Join(" ", payload.Select(byteValue => byteValue.ToString("X2", CultureInfo.InvariantCulture)));
        }

        private static string FormatByteArrayLiteral(byte[] payload)
        {
            if (payload.Length == 0)
            {
                return "Array.Empty<byte>()";
            }

            return $"new byte[] {{ {string.Join(", ", payload.Select(FormatByte))} }}";
        }

        private static string FormatByte(byte value) => $"0x{value:X2}";

        private static string FormatAddressList(IEnumerable<byte> addresses)
        {
            var list = addresses.ToList();
            return list.Count == 0 ? "(none)" : string.Join(", ", list.Select(address => address.ToString(CultureInfo.InvariantCulture)));
        }

        private static byte ParseByte(string? value, byte defaultValue)
        {
            return byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
        }

        private static string SanitizeIdentifier(string value)
        {
            var builder = new StringBuilder(value.Length);
            var capitalizeNext = true;
            foreach (var character in value)
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(capitalizeNext ? char.ToUpperInvariant(character) : character);
                    capitalizeNext = false;
                }
                else
                {
                    capitalizeNext = true;
                }
            }

            if (builder.Length == 0)
            {
                return "GeneratedEmulator";
            }

            if (!char.IsLetter(builder[0]) && builder[0] != '_')
            {
                builder.Insert(0, 'D');
            }

            return builder.ToString();
        }

        private static string JoinNonEmpty(string separator, params string[] values)
        {
            return string.Join(separator, values.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        private static string EscapeComment(string value)
        {
            return value.Replace("*/", "* /", StringComparison.Ordinal);
        }
    }
}
