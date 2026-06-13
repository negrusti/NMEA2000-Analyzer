using System.Globalization;
using System.Threading;

namespace NMEA2000Analyzer.Tools.Raymarine
{
    internal enum RaymarineI50CogSogSourceMode
    {
        Auto,
        Manual
    }

    internal sealed class RaymarineI50CogSogSourceCommand
    {
        private const uint RaymarineProprietaryPgn = 126720;
        private const byte DefaultPriority = 3;
        private static int _nextFastPacketSequenceId;

        public byte ToolSourceAddress { get; init; } = 254;
        public byte I50DestinationAddress { get; init; } = 255;
        public RaymarineI50CogSogSourceMode Mode { get; init; }
        public byte SelectedGpsSourceAddress { get; init; }

        public IReadOnlyList<byte[]> BuildPayloads()
        {
            return new[]
            {
                BuildGroupPrimePayload(),
                BuildSelectPayload()
            };
        }

        public async Task<RaymarineI50CogSogSourceProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            if (!PCAN.BeginMonitorSession(out var errorMessage))
            {
                return RaymarineI50CogSogSourceProbeResult.Failed(errorMessage);
            }

            if (!PCAN.BeginTransmitSession(out errorMessage))
            {
                PCAN.EndMonitorSession();
                return RaymarineI50CogSogSourceProbeResult.Failed(errorMessage);
            }

            var responseSourceAddress = I50DestinationAddress == 255 ? (byte?)null : I50DestinationAddress;
            var assembler = new ProbeFastPacketAssembler(ToolSourceAddress, responseSourceAddress);
            var responseCompletion = new TaskCompletionSource<RaymarineI50CogSogSourceProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Listener(MainWindow.Nmea2000Record record)
            {
                if (assembler.TryAccept(record, out var response))
                {
                    responseCompletion.TrySetResult(response);
                }
            }

            try
            {
                PCAN.RegisterMessageListener(Listener);

                var canId = CanBusUtilities.BuildCanId(
                    RaymarineProprietaryPgn,
                    I50DestinationAddress,
                    ToolSourceAddress,
                    DefaultPriority);

                foreach (var frame in BuildFastPacketFrames(BuildGroupPrimePayload()))
                {
                    if (!PCAN.TryTransmit(canId, frame, out errorMessage))
                    {
                        return RaymarineI50CogSogSourceProbeResult.Failed(errorMessage);
                    }

                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                }

                var completedTask = await Task.WhenAny(
                    responseCompletion.Task,
                    Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)).ConfigureAwait(false);

                if (completedTask == responseCompletion.Task)
                {
                    return await responseCompletion.Task.ConfigureAwait(false);
                }

                return RaymarineI50CogSogSourceProbeResult.Failed("No Raymarine F0 85 06 response received within 2 seconds.");
            }
            catch (OperationCanceledException)
            {
                return RaymarineI50CogSogSourceProbeResult.Failed("Probe cancelled.");
            }
            finally
            {
                PCAN.UnregisterMessageListener(Listener);
                PCAN.EndTransmitSession();
                PCAN.EndMonitorSession();
            }
        }

        public bool TrySend(out string statusMessage)
        {
            if (!PCAN.BeginTransmitSession(out var errorMessage))
            {
                statusMessage = errorMessage;
                return false;
            }

            try
            {
                var canId = CanBusUtilities.BuildCanId(
                    RaymarineProprietaryPgn,
                    I50DestinationAddress,
                    ToolSourceAddress,
                    DefaultPriority);

                foreach (var payload in BuildPayloads())
                {
                    foreach (var frame in BuildFastPacketFrames(payload))
                    {
                        if (!PCAN.TryTransmit(canId, frame, out errorMessage))
                        {
                            statusMessage = errorMessage;
                            return false;
                        }

                        Thread.Sleep(5);
                    }

                    Thread.Sleep(25);
                }

                statusMessage = Mode == RaymarineI50CogSogSourceMode.Auto
                    ? $"Sent Raymarine i50 COG/SOG source Auto command to destination {FormatAddress(I50DestinationAddress)}."
                    : $"Sent Raymarine i50 COG/SOG source command for GPS source {FormatAddress(SelectedGpsSourceAddress)} to destination {FormatAddress(I50DestinationAddress)}.";
                return true;
            }
            finally
            {
                PCAN.EndTransmitSession();
            }
        }

        public static string FormatPayloads(IEnumerable<byte[]> payloads)
        {
            return string.Join(
                Environment.NewLine,
                payloads.Select(payload => string.Join(" ", payload.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))));
        }

        private byte[] BuildGroupPrimePayload()
        {
            return new byte[]
            {
                0x3B, 0x9F, 0xF0, 0x85, 0x05,
                0x00, 0x00,
                0x00, 0x00, 0x00,
                0x02,
                0x00
            };
        }

        private byte[] BuildSelectPayload()
        {
            var lockFlag = Mode == RaymarineI50CogSogSourceMode.Manual ? (byte)0x01 : (byte)0x00;
            var selectedSource = Mode == RaymarineI50CogSogSourceMode.Manual ? SelectedGpsSourceAddress : (byte)0x00;
            var autoFlag1 = Mode == RaymarineI50CogSogSourceMode.Auto ? (byte)0x01 : (byte)0x00;
            var autoFlag2 = Mode == RaymarineI50CogSogSourceMode.Auto ? (byte)0xFF : (byte)0x00;

            return new byte[]
            {
                0x3B, 0x9F, 0xF0, 0x85, 0x07,
                0x00, 0x00, 0x00,
                0x00,
                0x01,
                0x00, 0x00, 0x00,
                lockFlag,
                selectedSource,
                autoFlag1,
                autoFlag2
            };
        }

        private static IReadOnlyList<byte[]> BuildFastPacketFrames(IReadOnlyList<byte> payload)
        {
            var frames = new List<byte[]>();
            var sequenceId = Interlocked.Increment(ref _nextFastPacketSequenceId) & 0x07;
            var offset = 0;

            var firstFrame = new byte[Math.Min(8, payload.Count + 2)];
            firstFrame[0] = (byte)(sequenceId << 5);
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

        private static string FormatAddress(byte address)
        {
            return address == 255
                ? "255 (broadcast)"
                : address.ToString(CultureInfo.InvariantCulture);
        }

        private sealed class ProbeFastPacketAssembler
        {
            private readonly byte _toolSourceAddress;
            private readonly byte? _responseSourceAddress;
            private readonly List<byte> _payload = new();
            private int? _sequenceId;
            private int _nextFrameIndex;
            private int _expectedPayloadLength;

            public ProbeFastPacketAssembler(byte toolSourceAddress, byte? responseSourceAddress)
            {
                _toolSourceAddress = toolSourceAddress;
                _responseSourceAddress = responseSourceAddress;
            }

            public bool TryAccept(MainWindow.Nmea2000Record record, out RaymarineI50CogSogSourceProbeResult response)
            {
                response = RaymarineI50CogSogSourceProbeResult.Failed(string.Empty);

                if (!string.Equals(record.PGN, RaymarineProprietaryPgn.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                    !byte.TryParse(record.Source, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceAddress) ||
                    !byte.TryParse(record.Destination, NumberStyles.Integer, CultureInfo.InvariantCulture, out var destinationAddress) ||
                    (_responseSourceAddress.HasValue && sourceAddress != _responseSourceAddress.Value) ||
                    destinationAddress != _toolSourceAddress)
                {
                    return false;
                }

                var frame = record.PayloadBytes;
                if (frame.Length < 2)
                {
                    return false;
                }

                var sequenceId = frame[0] >> 5;
                var frameIndex = frame[0] & 0x1F;
                if (frameIndex == 0)
                {
                    _sequenceId = sequenceId;
                    _nextFrameIndex = 1;
                    _expectedPayloadLength = frame[1];
                    _payload.Clear();
                    AppendPayloadBytes(frame, 2);
                }
                else if (_sequenceId == sequenceId && frameIndex == _nextFrameIndex)
                {
                    _nextFrameIndex++;
                    AppendPayloadBytes(frame, 1);
                }
                else
                {
                    _sequenceId = null;
                    _payload.Clear();
                    return false;
                }

                if (_expectedPayloadLength <= 0 || _payload.Count < _expectedPayloadLength)
                {
                    return false;
                }

                var payload = _payload.Take(_expectedPayloadLength).ToArray();
                if (payload.Length < 12 ||
                    payload[2] != 0xF0 ||
                    payload[3] != 0x85 ||
                    payload[4] != 0x06)
                {
                    return false;
                }

                response = RaymarineI50CogSogSourceProbeResult.FromResponse(sourceAddress, payload);
                return true;
            }

            private void AppendPayloadBytes(IReadOnlyList<byte> frame, int startIndex)
            {
                for (var i = startIndex; i < frame.Count && _payload.Count < _expectedPayloadLength; i++)
                {
                    _payload.Add(frame[i]);
                }
            }
        }
    }

    internal sealed class RaymarineI50CogSogSourceProbeResult
    {
        private RaymarineI50CogSogSourceProbeResult(bool success, string message, byte sourceAddress, byte[] payload)
        {
            Success = success;
            Message = message;
            SourceAddress = sourceAddress;
            Payload = payload;
        }

        public bool Success { get; }
        public string Message { get; }
        public byte SourceAddress { get; }
        public byte[] Payload { get; }

        public static RaymarineI50CogSogSourceProbeResult FromResponse(byte sourceAddress, byte[] payload)
        {
            var group = payload.Length >= 10
                ? $"{payload[7]:X2} {payload[8]:X2} {payload[9]:X2}"
                : "n/a";
            var status = payload.Length >= 12
                ? $"{payload[10]:X2} {payload[11]:X2}"
                : "n/a";

            return new RaymarineI50CogSogSourceProbeResult(
                true,
                $"Received F0 85 06 from source {sourceAddress}. Group bytes: {group}. Status bytes: {status}.",
                sourceAddress,
                payload);
        }

        public static RaymarineI50CogSogSourceProbeResult Failed(string message)
        {
            return new RaymarineI50CogSogSourceProbeResult(false, message, 255, Array.Empty<byte>());
        }
    }
}
