using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using static NMEA2000Analyzer.MainWindow;

namespace NMEA2000Analyzer
{
    public sealed class DeviceEmulationPlan
    {
        public required byte OriginalSourceAddress { get; init; }
        public required string DeviceLabel { get; init; }
        public required IReadOnlyList<DeviceReplayFrame> IdentityFrames { get; init; }
        public required IReadOnlyList<DeviceReplayFrame> RoutineFrames { get; init; }
        public required IReadOnlyList<DeviceDestinationProfile> DestinationProfiles { get; init; }
        public required TimeSpan RoutineCycleLength { get; init; }
    }

    public sealed class DeviceReplayFrame
    {
        public required Nmea2000Record Record { get; init; }
        public required TimeSpan Offset { get; init; }
    }

    public sealed class DeviceDestinationProfile
    {
        public required byte OriginalAddress { get; init; }
        public required string Label { get; init; }
    }

    public sealed class DeviceDestinationMapping
    {
        public required byte OriginalAddress { get; init; }
        public required string Label { get; init; }
        public string NewAddress { get; set; } = string.Empty;
    }

    public sealed class DeviceBusAddressOption
    {
        public required string AddressText { get; init; }
        public required string Label { get; init; }
    }

    internal static class DeviceEmulationPlanBuilder
    {
        private static readonly Dictionary<string, int> IdentityPgnOrder = new(StringComparer.Ordinal)
        {
            ["60928"] = 0,
            ["126996"] = 1,
            ["126998"] = 2,
            ["126464"] = 3
        };

        private static readonly HashSet<string> IdentityPgns = new(StringComparer.Ordinal)
        {
            "60928",
            "126464",
            "126996",
            "126998"
        };

        private static readonly HashSet<string> ExcludedRoutinePgns = new(StringComparer.Ordinal)
        {
            "59392",
            "59904",
            "60160",
            "60416",
            "60928",
            "126208",
            "126464",
            "126996",
            "126998"
        };

        public static DeviceEmulationPlan? Build(
            DeviceStatisticsEntry entry,
            IReadOnlyList<Nmea2000Record>? rawRecords,
            IReadOnlyList<Nmea2000Record>? assembledRecords)
        {
            if (rawRecords == null || rawRecords.Count == 0 || assembledRecords == null || assembledRecords.Count == 0)
            {
                return null;
            }

            var sourceText = entry.Address.ToString(CultureInfo.InvariantCulture);
            var sourceRawRecords = rawRecords
                .Where(record => string.Equals(record.Source, sourceText, StringComparison.Ordinal))
                .OrderBy(record => record.LogSequenceNumber)
                .ToList();
            var sourceAssembledRecords = assembledRecords
                .Where(record => string.Equals(record.Source, sourceText, StringComparison.Ordinal))
                .OrderBy(record => record.LogSequenceNumber)
                .ToList();

            if (sourceRawRecords.Count == 0 || sourceAssembledRecords.Count == 0)
            {
                return null;
            }

            var identityFrames = BuildReplayFrames(sourceRawRecords
                .Where(record => IdentityPgns.Contains(record.PGN))
                .OrderBy(record => IdentityPgnOrder.GetValueOrDefault(record.PGN, int.MaxValue))
                .ThenBy(record => record.LogSequenceNumber)
                .ToList(), TimeSpan.FromMilliseconds(80));

            var routinePgns = GetRoutinePgns(sourceAssembledRecords);
            var routineFrames = BuildReplayFrames(sourceRawRecords
                .Where(record => routinePgns.Contains(record.PGN))
                .ToList(), TimeSpan.FromMilliseconds(100));

            if (identityFrames.Count == 0 && routineFrames.Count == 0)
            {
                return null;
            }

            var destinationProfiles = sourceRawRecords
                .Where(record => CanBusUtilities.IsDestinationSpecificPgn(record.PGN))
                .Select(record => CanBusUtilities.ParseAddressOrDefault(record.Destination, 255))
                .Where(address => address != 255)
                .Distinct()
                .OrderBy(address => address)
                .Select(address => new DeviceDestinationProfile
                {
                    OriginalAddress = address,
                    Label = BuildDestinationLabel(address)
                })
                .ToList();

            return new DeviceEmulationPlan
            {
                OriginalSourceAddress = (byte)entry.Address,
                DeviceLabel = BuildDeviceLabel(entry),
                IdentityFrames = identityFrames,
                RoutineFrames = routineFrames,
                DestinationProfiles = destinationProfiles,
                RoutineCycleLength = EstimateCycleLength(routineFrames)
            };
        }

        private static string BuildDeviceLabel(DeviceStatisticsEntry entry)
        {
            var label = JoinNonEmpty(" ",
                entry.MfgCode ?? string.Empty,
                entry.ModelID ?? string.Empty);

            return string.IsNullOrWhiteSpace(label)
                ? $"Device {entry.Address}"
                : label;
        }

        private static string BuildDestinationLabel(byte address)
        {
            if (Globals.Devices.TryGetValue(address, out var device))
            {
                var name = FirstNonEmpty(
                    device.ModelID ?? string.Empty,
                    device.ModelVersion ?? string.Empty,
                    device.MfgCode ?? string.Empty);
                return string.IsNullOrWhiteSpace(name)
                    ? $"Addr {address}"
                    : $"Addr {address} - {name}";
            }

            return $"Addr {address}";
        }

        private static IReadOnlyList<DeviceReplayFrame> BuildReplayFrames(
            IReadOnlyList<Nmea2000Record> records,
            TimeSpan fallbackStep)
        {
            if (records.Count == 0)
            {
                return Array.Empty<DeviceReplayFrame>();
            }

            var timestamped = records
                .Select(record => new
                {
                    Record = record,
                    Timestamp = TryParseRecordTimestamp(record.Timestamp)
                })
                .ToList();

            if (timestamped.All(item => item.Timestamp.HasValue))
            {
                var firstTimestamp = timestamped[0].Timestamp!.Value;
                return timestamped
                    .Select(item => new DeviceReplayFrame
                    {
                        Record = item.Record,
                        Offset = item.Timestamp!.Value - firstTimestamp
                    })
                    .ToList();
            }

            return records
                .Select((record, index) => new DeviceReplayFrame
                {
                    Record = record,
                    Offset = TimeSpan.FromTicks(fallbackStep.Ticks * index)
                })
                .ToList();
        }

        private static HashSet<string> GetRoutinePgns(IReadOnlyList<Nmea2000Record> records)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);

            foreach (var group in records.GroupBy(record => record.PGN, StringComparer.Ordinal))
            {
                if (ExcludedRoutinePgns.Contains(group.Key) || group.Count() < 3)
                {
                    continue;
                }

                if (IsRegularByDefinition(group) || IsRegularByObservedIntervals(group))
                {
                    result.Add(group.Key);
                }
            }

            return result;
        }

        private static bool IsRegularByDefinition(IGrouping<string, Nmea2000Record> group)
        {
            var definition = GetPgnDefinition(group.First());
            return definition != null &&
                (!definition.TransmissionIrregular || definition.TransmissionInterval > 0);
        }

        private static bool IsRegularByObservedIntervals(IGrouping<string, Nmea2000Record> group)
        {
            var timestamps = group
                .Select(record => TryParseRecordTimestamp(record.Timestamp))
                .Where(timestamp => timestamp.HasValue)
                .Select(timestamp => timestamp!.Value)
                .OrderBy(timestamp => timestamp)
                .ToList();

            if (timestamps.Count < 3)
            {
                return false;
            }

            var intervals = timestamps
                .Zip(timestamps.Skip(1), (first, second) => (second - first).TotalSeconds)
                .Where(interval => interval > 0)
                .ToList();

            if (intervals.Count < 2)
            {
                return false;
            }

            var average = intervals.Average();
            if (average <= 0)
            {
                return false;
            }

            var variance = intervals.Sum(interval => Math.Pow(interval - average, 2)) / intervals.Count;
            var standardDeviation = Math.Sqrt(variance);
            return (standardDeviation / average) <= 0.35;
        }

        private static Canboat.Pgn? GetPgnDefinition(Nmea2000Record record)
        {
            var canboatRoot = ((App)Application.Current).CanboatRoot;
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

            return uint.TryParse(record.PGN, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pgn)
                ? canboatRoot.PGNs.FirstOrDefault(candidate => candidate.PGN == pgn)
                : null;
        }

        private static TimeSpan EstimateCycleLength(IReadOnlyList<DeviceReplayFrame> routineFrames)
        {
            if (routineFrames.Count == 0)
            {
                return TimeSpan.FromSeconds(1);
            }

            if (routineFrames.Count == 1)
            {
                return TimeSpan.FromSeconds(1);
            }

            var positiveGaps = routineFrames
                .Zip(routineFrames.Skip(1), (first, second) => second.Offset - first.Offset)
                .Where(gap => gap > TimeSpan.Zero)
                .OrderBy(gap => gap)
                .ToList();

            var trailingGap = positiveGaps.Count == 0
                ? TimeSpan.FromSeconds(1)
                : positiveGaps[positiveGaps.Count / 2];

            var lastOffset = routineFrames[^1].Offset;
            var cycleLength = lastOffset + trailingGap;
            return cycleLength > TimeSpan.Zero ? cycleLength : TimeSpan.FromSeconds(1);
        }

        private static DateTimeOffset? TryParseRecordTimestamp(string? timestamp)
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

            if (DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var absoluteTimestamp))
            {
                return absoluteTimestamp;
            }

            if (TimeSpan.TryParse(timestamp, CultureInfo.InvariantCulture, out var elapsed))
            {
                return DateTimeOffset.UnixEpoch.Add(elapsed);
            }

            return null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        private static string JoinNonEmpty(string separator, params string[] values)
        {
            return string.Join(separator, values.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
    }

    internal sealed class DeviceEmulationService
    {
        private const string IsoRequestPgn = "59904";
        private readonly DeviceEmulationPlan _plan;
        private readonly object _stateSyncRoot = new();
        private CancellationTokenSource? _cts;
        private Task? _runTask;
        private string _lastError = string.Empty;
        private byte _activeSourceAddress;
        private Dictionary<byte, byte> _activeDestinationMap = new();
        private bool _listenerRegistered;

        public DeviceEmulationService(DeviceEmulationPlan plan)
        {
            _plan = plan;
        }

        public bool IsRunning => _runTask != null && !_runTask.IsCompleted;
        public string LastError => _lastError;
        public event Action<string>? StatusChanged;

        public bool Start(byte sourceAddress, IReadOnlyDictionary<byte, byte> destinationMap, out string errorMessage)
        {
            if (IsRunning)
            {
                errorMessage = "Emulation is already running.";
                return false;
            }

            if (!PCAN.BeginTransmitSession(out errorMessage))
            {
                return false;
            }

            PcanTraceLog.BeginScope("device emulation");
            _lastError = string.Empty;
            lock (_stateSyncRoot)
            {
                _activeSourceAddress = sourceAddress;
                _activeDestinationMap = new Dictionary<byte, byte>(destinationMap);
                if (!_listenerRegistered)
                {
                    PCAN.RegisterMessageListener(OnBusMessageReceived);
                    _listenerRegistered = true;
                }
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _runTask = Task.Run(() => RunAsync(sourceAddress, destinationMap, token), token);
            StatusChanged?.Invoke($"Running as source {sourceAddress}.");
            return true;
        }

        public async Task StopAsync()
        {
            if (!IsRunning)
            {
                return;
            }

            _cts?.Cancel();
            try
            {
                if (_runTask != null)
                {
                    await _runTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task RunAsync(byte sourceAddress, IReadOnlyDictionary<byte, byte> destinationMap, CancellationToken cancellationToken)
        {
            try
            {
                await SendFramesAsync(GetStartupIdentityFrames(), sourceAddress, destinationMap, applyDestinationMap: false, cancellationToken).ConfigureAwait(false);

                if (_plan.RoutineFrames.Count == 0)
                {
                    StatusChanged?.Invoke("Identity packets sent. No routine packets available to replay.");
                    return;
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    var cycleStart = Stopwatch.GetTimestamp();
                    foreach (var frame in _plan.RoutineFrames)
                    {
                        await DelayUntilAsync(cycleStart, frame.Offset, cancellationToken).ConfigureAwait(false);
                        TransmitFrame(frame.Record, sourceAddress, destinationMap, applyDestinationMap: true);
                    }

                    await DelayUntilAsync(cycleStart, _plan.RoutineCycleLength, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                StatusChanged?.Invoke("Emulation stopped.");
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                StatusChanged?.Invoke($"Emulation stopped: {ex.Message}");
            }
            finally
            {
                lock (_stateSyncRoot)
                {
                    if (_listenerRegistered)
                    {
                        PCAN.UnregisterMessageListener(OnBusMessageReceived);
                        _listenerRegistered = false;
                    }
                }

                PCAN.EndTransmitSession();
                PcanTraceLog.EndScope("device emulation");
            }
        }

        private async Task SendFramesAsync(
            IReadOnlyList<DeviceReplayFrame> frames,
            byte sourceAddress,
            IReadOnlyDictionary<byte, byte> destinationMap,
            bool applyDestinationMap,
            CancellationToken cancellationToken)
        {
            Nmea2000Record? previousRecord = null;
            foreach (var frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TransmitFrame(frame.Record, sourceAddress, destinationMap, applyDestinationMap);
                var delay = GetIdentityFrameDelay(previousRecord, frame.Record);
                previousRecord = frame.Record;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        private void TransmitFrame(
            Nmea2000Record record,
            byte sourceAddress,
            IReadOnlyDictionary<byte, byte> destinationMap,
            bool applyDestinationMap)
        {
            var destination = CanBusUtilities.ParseAddressOrDefault(record.Destination, 255);
            if (applyDestinationMap &&
                CanBusUtilities.IsDestinationSpecificPgn(record.PGN) &&
                destinationMap.TryGetValue(destination, out var remappedDestination))
            {
                destination = remappedDestination;
            }

            var canId = CanBusUtilities.BuildCanId(record, sourceAddress, destination);
            if (!TryTransmitRecord(canId, record, out var errorMessage))
            {
                throw new InvalidOperationException(errorMessage);
            }
        }

        private void OnBusMessageReceived(Nmea2000Record record)
        {
            try
            {
                if (!IsRunning || !string.Equals(record.PGN, IsoRequestPgn, StringComparison.Ordinal))
                {
                    return;
                }

                var payload = record.PayloadBytes;
                if (payload == null || payload.Length < 3)
                {
                    return;
                }

                var requestedPgn = ((uint)payload[2] << 16) | ((uint)payload[1] << 8) | payload[0];
                var requestedPgnText = requestedPgn.ToString(CultureInfo.InvariantCulture);
                var destination = CanBusUtilities.ParseAddressOrDefault(record.Destination, 255);

                byte sourceAddress;
                Dictionary<byte, byte> destinationMap;
                lock (_stateSyncRoot)
                {
                    sourceAddress = _activeSourceAddress;
                    destinationMap = new Dictionary<byte, byte>(_activeDestinationMap);
                }

                if (destination != 255 && destination != sourceAddress)
                {
                    return;
                }

                var requesterAddress = CanBusUtilities.ParseAddressOrDefault(record.Source, 255);
                var matchingFrames = _plan.IdentityFrames
                    .Where(frame => string.Equals(frame.Record.PGN, requestedPgnText, StringComparison.Ordinal))
                    .ToList();
                if (matchingFrames.Count == 0)
                {
                    return;
                }

                foreach (var frame in matchingFrames)
                {
                    var replyDestination = CanBusUtilities.IsDestinationSpecificPgn(frame.Record.PGN)
                        ? requesterAddress
                        : CanBusUtilities.ParseAddressOrDefault(frame.Record.Destination, 255);
                    var canId = CanBusUtilities.BuildCanId(frame.Record, sourceAddress, replyDestination);
                    if (!TryTransmitRecord(canId, frame.Record, out var errorMessage))
                    {
                        StatusChanged?.Invoke($"Failed to answer request for PGN {requestedPgnText}: {errorMessage}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Request handling error: {ex.Message}");
            }
        }

        private static async Task DelayUntilAsync(long cycleStartTicks, TimeSpan targetOffset, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elapsed = Stopwatch.GetElapsedTime(cycleStartTicks);
                var remaining = targetOffset - elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return;
                }

                var delay = remaining > TimeSpan.FromMilliseconds(50)
                    ? TimeSpan.FromMilliseconds(50)
                    : remaining;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        private static TimeSpan GetIdentityFrameDelay(Nmea2000Record? previousRecord, Nmea2000Record currentRecord)
        {
            if (previousRecord == null)
            {
                return TimeSpan.FromMilliseconds(20);
            }

            if (string.Equals(previousRecord.PGN, currentRecord.PGN, StringComparison.Ordinal) &&
                string.Equals(previousRecord.Source, currentRecord.Source, StringComparison.Ordinal) &&
                string.Equals(previousRecord.Destination, currentRecord.Destination, StringComparison.Ordinal) &&
                IsFastPacketPgn(currentRecord.PGN))
            {
                return TimeSpan.FromMilliseconds(5);
            }

            if (string.Equals(currentRecord.PGN, "60928", StringComparison.Ordinal))
            {
                return TimeSpan.FromMilliseconds(100);
            }

            return TimeSpan.FromMilliseconds(25);
        }

        private static bool IsFastPacketPgn(string? pgnText)
        {
            if (pgnText == null || !int.TryParse(pgnText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pgn))
            {
                return false;
            }

            return Globals.UniquePGNs.TryGetValue(pgn, out var pgnDefinition) &&
                string.Equals(pgnDefinition.Type, "Fast", StringComparison.OrdinalIgnoreCase);
        }

        private IReadOnlyList<DeviceReplayFrame> GetStartupIdentityFrames()
        {
            var startupFrames = _plan.IdentityFrames
                .Where(frame => string.Equals(frame.Record.PGN, "60928", StringComparison.Ordinal))
                .ToList();

            return startupFrames.Count == 0
                ? _plan.IdentityFrames
                : startupFrames;
        }

        private static bool TryTransmitRecord(uint canId, Nmea2000Record record, out string errorMessage)
        {
            var payload = record.PayloadBytes;
            if (payload.Length <= 8)
            {
                return PCAN.TryTransmit(canId, payload, out errorMessage);
            }

            if (!IsFastPacketPgn(record.PGN))
            {
                errorMessage = $"PGN {record.PGN} has payload length {payload.Length}, which exceeds 8 bytes and is not a fast packet.";
                return false;
            }

            var frames = BuildFastPacketFrames(payload);
            foreach (var framePayload in frames)
            {
                if (!PCAN.TryTransmit(canId, framePayload, out errorMessage))
                {
                    return false;
                }
            }

            errorMessage = string.Empty;
            return true;
        }

        private static IReadOnlyList<byte[]> BuildFastPacketFrames(IReadOnlyList<byte> payload)
        {
            var frames = new List<byte[]>();
            var sequenceId = 0;
            var offset = 0;

            var firstFrame = new byte[Math.Min(8, payload.Count + 2)];
            firstFrame[0] = (byte)sequenceId;
            firstFrame[1] = (byte)payload.Count;
            var firstFramePayloadBytes = Math.Min(6, payload.Count);
            for (var i = 0; i < firstFramePayloadBytes; i++)
            {
                firstFrame[i + 2] = payload[i];
            }

            frames.Add(firstFrame);
            offset += firstFramePayloadBytes;

            var frameIndex = 1;
            while (offset < payload.Count)
            {
                var remaining = payload.Count - offset;
                var framePayloadBytes = Math.Min(7, remaining);
                var frame = new byte[framePayloadBytes + 1];
                frame[0] = (byte)((sequenceId << 5) | (frameIndex & 0x1F));
                for (var i = 0; i < framePayloadBytes; i++)
                {
                    frame[i + 1] = payload[offset + i];
                }

                frames.Add(frame);
                offset += framePayloadBytes;
                frameIndex++;
            }

            return frames;
        }
    }
}
