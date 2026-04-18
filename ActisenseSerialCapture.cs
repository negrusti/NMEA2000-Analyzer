using System.Globalization;
using System.IO.Ports;
using System.Threading;
using static NMEA2000Analyzer.MainWindow;

namespace NMEA2000Analyzer
{
    internal static class ActisenseSerialCapture
    {
        // Captured from Actisense NMEA Reader startup traffic to an NGT-1.
        private static readonly byte[] StartupControlCommands =
        {
            0x11, 0x42, 0x10, 0x41, 0x43, 0x44, 0x45,
            0x13, 0x12, 0x16, 0x40, 0x4E, 0x4F, 0x4D
        };

        private static readonly TimeSpan[] StartupControlDelays =
        {
            TimeSpan.FromMilliseconds(501),
            TimeSpan.FromMilliseconds(494),
            TimeSpan.FromMilliseconds(249),
            TimeSpan.FromMilliseconds(251),
            TimeSpan.FromMilliseconds(203),
            TimeSpan.FromMilliseconds(166),
            TimeSpan.FromMilliseconds(1006),
            TimeSpan.FromMilliseconds(311),
            TimeSpan.FromMilliseconds(126),
            TimeSpan.FromMilliseconds(249),
            TimeSpan.FromMilliseconds(501),
            TimeSpan.FromMilliseconds(376),
            TimeSpan.FromMilliseconds(498)
        };

        private const byte Dle = 0x10;
        private const byte Stx = 0x02;
        private const byte Etx = 0x03;
        private const byte N2kMsgReceived = 0x93;
        private const byte N2kMsgSend = 0x94;
        private const byte NgtMsgSend = 0xA1;
        private const int SerialReadBufferSize = 1024 * 1024;
        private const int SerialReadChunkSize = 4096;

        private const uint IsoRequestPgn = 59904;
        private const uint ProductInformationPgn = 126996;
        private const byte BroadcastAddress = 255;
        private const byte RequestPriority = 6;

        private static readonly object SyncRoot = new();
        private static readonly object WriteSyncRoot = new();
        private static string? _activePortName;

        private static SerialPort? _port;
        private static CancellationTokenSource? _captureCancellation;
        private static Task? _readerTask;
        private static Task? _startupTask;
        private static List<Nmea2000Record>? _capture;
        private static int _capturedCount;

        public static IReadOnlyList<string> GetAvailablePorts()
        {
            return SerialPort.GetPortNames()
                .OrderBy(port => port, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static string? GetActivePortName()
        {
            return _activePortName;
        }

        public static string? AutoDetectPort()
        {
            var ports = GetAvailablePorts();
            foreach (var portName in ports)
            {
                if (ProbePort(portName))
                {
                    return portName;
                }
            }

            return null;
        }

        public static bool StartCapture(string portName, int baudRate = 115200)
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                return false;
            }

            try
            {
                StopCapture();

                lock (SyncRoot)
                {
                    if (_capture == null)
                    {
                        _capture = new List<Nmea2000Record>();
                    }
                    else
                    {
                        _capture.Clear();
                    }
                }

                _capturedCount = 0;
                _captureCancellation = new CancellationTokenSource();
                _port = CreatePort(portName, baudRate);
                _port.Open();
                _activePortName = portName;

                _readerTask = Task.Run(() => ReadLoop(_captureCancellation.Token), _captureCancellation.Token);
                _startupTask = Task.Run(() => SendStartupControlSequence(_captureCancellation.Token), _captureCancellation.Token);

                return true;
            }
            catch
            {
                StopCapture();
                return false;
            }
        }

        public static void StopCapture()
        {
            if (_captureCancellation != null)
            {
                try
                {
                    _captureCancellation.Cancel();
                }
                catch
                {
                }
            }

            try
            {
                _readerTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }

            try
            {
                _startupTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }

            _readerTask = null;
            _startupTask = null;
            _activePortName = null;

            if (_port != null)
            {
                try
                {
                    if (_port.IsOpen)
                    {
                        _port.Close();
                    }
                }
                catch
                {
                }

                _port.Dispose();
                _port = null;
            }

            _captureCancellation?.Dispose();
            _captureCancellation = null;
        }

        public static List<Nmea2000Record> LoadCapture()
        {
            lock (SyncRoot)
            {
                return _capture == null
                    ? new List<Nmea2000Record>()
                    : new List<Nmea2000Record>(_capture);
            }
        }

        public static int GetCapturedCount()
        {
            return Volatile.Read(ref _capturedCount);
        }

        public static bool RequestProductInformationBroadcast(out string errorMessage)
        {
            try
            {
                var requestPayload = new byte[]
                {
                    RequestPriority,
                    (byte)(IsoRequestPgn & 0xFF),
                    (byte)((IsoRequestPgn >> 8) & 0xFF),
                    (byte)((IsoRequestPgn >> 16) & 0xFF),
                    BroadcastAddress,
                    3,
                    (byte)(ProductInformationPgn & 0xFF),
                    (byte)((ProductInformationPgn >> 8) & 0xFF),
                    (byte)((ProductInformationPgn >> 16) & 0xFF)
                };

                WriteMessage(N2kMsgSend, requestPayload);
                errorMessage = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static SerialPort CreatePort(string portName, int baudRate)
        {
            return new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                ReadBufferSize = SerialReadBufferSize,
                ReadTimeout = 500,
                WriteTimeout = 1000,
                DtrEnable = false,
                RtsEnable = false
            };
        }

        private static void SendStartupControlSequence(CancellationToken cancellationToken)
        {
            try
            {
                for (var i = 0; i < StartupControlCommands.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteMessage(NgtMsgSend, new[] { StartupControlCommands[i] });

                    if (i < StartupControlDelays.Length)
                    {
                        Task.Delay(StartupControlDelays[i], cancellationToken).Wait(cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        private static void WriteMessage(byte command, IReadOnlyList<byte> payload)
        {
            if (_port == null || !_port.IsOpen)
            {
                throw new InvalidOperationException("Actisense serial port is not open.");
            }

            if (payload.Count > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(payload), "Payload is too large for an Actisense frame.");
            }

            var frame = new List<byte>(payload.Count + 8)
            {
                Dle,
                Stx,
                command
            };

            byte checksum = command;
            byte length = (byte)payload.Count;
            AppendEscaped(frame, length);
            checksum += length;

            foreach (var value in payload)
            {
                checksum += value;
                AppendEscaped(frame, value);
            }

            checksum = unchecked((byte)(256 - checksum));
            AppendEscaped(frame, checksum);
            frame.Add(Dle);
            frame.Add(Etx);

            lock (WriteSyncRoot)
            {
                _port.Write(frame.ToArray(), 0, frame.Count);
            }
        }

        private static void AppendEscaped(List<byte> frame, byte value)
        {
            if (value == Dle)
            {
                frame.Add(Dle);
            }

            frame.Add(value);
        }

        private static void ReadLoop(CancellationToken cancellationToken)
        {
            if (_port == null)
            {
                return;
            }

            var messageBuffer = new List<byte>(256);
            bool awaitingFrameStart = true;
            bool inFrame = false;
            bool escapePending = false;
            var readBuffer = new byte[SerialReadChunkSize];

            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = _port.Read(readBuffer, 0, readBuffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch
                {
                    break;
                }

                if (bytesRead <= 0)
                {
                    continue;
                }

                for (var i = 0; i < bytesRead; i++)
                {
                    var current = readBuffer[i];

                    if (awaitingFrameStart)
                    {
                        if (current == Dle)
                        {
                            escapePending = true;
                            awaitingFrameStart = false;
                        }

                        continue;
                    }

                    if (!inFrame)
                    {
                        if (escapePending && current == Stx)
                        {
                            messageBuffer.Clear();
                            inFrame = true;
                            escapePending = false;
                            continue;
                        }

                        awaitingFrameStart = current != Dle;
                        escapePending = current == Dle;
                        continue;
                    }

                    if (escapePending)
                    {
                        if (current == Dle)
                        {
                            messageBuffer.Add(Dle);
                            escapePending = false;
                            continue;
                        }

                        if (current == Etx)
                        {
                            ProcessIncomingMessage(messageBuffer);
                            messageBuffer.Clear();
                            inFrame = false;
                            awaitingFrameStart = true;
                            escapePending = false;
                            continue;
                        }

                        if (current == Stx)
                        {
                            messageBuffer.Clear();
                            escapePending = false;
                            continue;
                        }

                        messageBuffer.Clear();
                        inFrame = false;
                        awaitingFrameStart = true;
                        escapePending = false;
                        continue;
                    }

                    if (current == Dle)
                    {
                        escapePending = true;
                        continue;
                    }

                    messageBuffer.Add(current);
                }
            }
        }

        private static void ProcessIncomingMessage(List<byte> frameBytes)
        {
            if (frameBytes.Count < 3)
            {
                return;
            }

            byte checksum = 0;
            foreach (var value in frameBytes)
            {
                checksum += value;
            }

            if (checksum != 0)
            {
                return;
            }

            byte command = frameBytes[0];
            int payloadLength = frameBytes[1];

            if (payloadLength < 0 || frameBytes.Count < payloadLength + 3)
            {
                return;
            }

            if (command != N2kMsgReceived)
            {
                return;
            }

            var payload = frameBytes.Skip(2).Take(payloadLength).ToArray();
            ProcessReceivedN2kMessage(payload);
        }

        private static void ProcessReceivedN2kMessage(byte[] payload)
        {
            const int receivedHeaderLength = 11;
            if (payload.Length < receivedHeaderLength)
            {
                return;
            }

            var priority = payload[0];
            var pgn = payload[1] | (payload[2] << 8) | (payload[3] << 16);
            var destination = payload[4];
            var source = payload[5];
            var dataLength = (int)payload[10];

            if (dataLength > payload.Length - receivedHeaderLength)
            {
                dataLength = payload.Length - receivedHeaderLength;
            }

            var data = payload
                .Skip(receivedHeaderLength)
                .Take(dataLength)
                .ToArray();

            var record = new Nmea2000Record
            {
                Timestamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                Source = source.ToString(CultureInfo.InvariantCulture),
                Destination = destination.ToString(CultureInfo.InvariantCulture),
                PGN = pgn.ToString(CultureInfo.InvariantCulture),
                Priority = priority.ToString(CultureInfo.InvariantCulture),
                PayloadBytes = data
            };

            lock (SyncRoot)
            {
                _capture ??= new List<Nmea2000Record>();
                _capture.Add(record);
            }

            Interlocked.Increment(ref _capturedCount);
        }

        private static bool ProbePort(string portName)
        {
            try
            {
                using var port = CreatePort(portName, 115200);
                port.ReadTimeout = 250;
                port.WriteTimeout = 500;
                port.Open();

                var frame = BuildMessageFrame(NgtMsgSend, new[] { StartupControlCommands[0] });
                port.Write(frame, 0, frame.Length);

                var deadline = DateTime.UtcNow.AddSeconds(2);
                var messageBuffer = new List<byte>(256);
                bool awaitingFrameStart = true;
                bool inFrame = false;
                bool escapePending = false;

                while (DateTime.UtcNow < deadline)
                {
                    int nextByte;
                    try
                    {
                        nextByte = port.ReadByte();
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }

                    if (nextByte < 0)
                    {
                        continue;
                    }

                    var current = (byte)nextByte;

                    if (awaitingFrameStart)
                    {
                        if (current == Dle)
                        {
                            escapePending = true;
                            awaitingFrameStart = false;
                        }

                        continue;
                    }

                    if (!inFrame)
                    {
                        if (escapePending && current == Stx)
                        {
                            messageBuffer.Clear();
                            inFrame = true;
                            escapePending = false;
                            continue;
                        }

                        awaitingFrameStart = current != Dle;
                        escapePending = current == Dle;
                        continue;
                    }

                    if (escapePending)
                    {
                        if (current == Dle)
                        {
                            messageBuffer.Add(Dle);
                            escapePending = false;
                            continue;
                        }

                        if (current == Etx)
                        {
                            if (IsValidActisenseN2kFrame(messageBuffer))
                            {
                                return true;
                            }

                            messageBuffer.Clear();
                            inFrame = false;
                            awaitingFrameStart = true;
                            escapePending = false;
                            continue;
                        }

                        if (current == Stx)
                        {
                            messageBuffer.Clear();
                            escapePending = false;
                            continue;
                        }

                        messageBuffer.Clear();
                        inFrame = false;
                        awaitingFrameStart = true;
                        escapePending = false;
                        continue;
                    }

                    if (current == Dle)
                    {
                        escapePending = true;
                        continue;
                    }

                    messageBuffer.Add(current);
                }
            }
            catch
            {
            }

            return false;
        }

        private static byte[] BuildMessageFrame(byte command, IReadOnlyList<byte> payload)
        {
            var frame = new List<byte>(payload.Count + 8)
            {
                Dle,
                Stx,
                command
            };

            byte checksum = command;
            byte length = (byte)payload.Count;
            AppendEscaped(frame, length);
            checksum += length;

            foreach (var value in payload)
            {
                checksum += value;
                AppendEscaped(frame, value);
            }

            checksum = unchecked((byte)(256 - checksum));
            AppendEscaped(frame, checksum);
            frame.Add(Dle);
            frame.Add(Etx);
            return frame.ToArray();
        }

        private static bool IsValidActisenseN2kFrame(List<byte> frameBytes)
        {
            if (frameBytes.Count < 3)
            {
                return false;
            }

            byte checksum = 0;
            foreach (var value in frameBytes)
            {
                checksum += value;
            }

            if (checksum != 0)
            {
                return false;
            }

            byte command = frameBytes[0];
            if (command != N2kMsgReceived)
            {
                return false;
            }

            int payloadLength = frameBytes[1];
            return frameBytes.Count >= payloadLength + 3;
        }
    }
}
