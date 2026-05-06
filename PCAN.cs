using Peak.Can.Basic;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using static NMEA2000Analyzer.MainWindow;

namespace NMEA2000Analyzer
{
    class PCAN
    {
        private const uint IsoRequestPgn = 59904;
        private const uint ProductInformationPgn = 126996;
        private const byte BroadcastAddress = 255;
        private const byte RequestSourceAddress = 254;
        private const byte RequestPriority = 6;
        private static readonly Worker _worker = new Worker(PcanChannel.Usb01, Bitrate.Pcan250);
        private static readonly object SessionSyncRoot = new();
        private static readonly List<Action<Nmea2000Record>> _messageListeners = new();
        private static List<Nmea2000Record>? capture;
        private static int capturedCount;
        private static bool _workerStarted;
        private static bool _captureRunning;
        private static int _transmitSessionCount;
        private static int _monitorSessionCount;
        
        public static bool StartCapture()
        {
            try
            {
                PcanTraceLog.LogNote("capture start");
                if (capture == null)
                {
                    capture = new List<Nmea2000Record>();
                }
                else
                {
                    capture.Clear();
                }

                capturedCount = 0;
                lock (SessionSyncRoot)
                {
                    EnsureWorkerStarted();
                    _captureRunning = true;
                }
                Debug.WriteLine("Worker started successfully.");
                return (true);
            }
            catch (PcanBasicException)
            {
                return (false);
            }
            catch (DllNotFoundException)
            {
                return (false);
            }
        }
        public static void StopCapture()
        {
            try
            {
                PcanTraceLog.LogNote("capture stop");
                lock (SessionSyncRoot)
                {
                    if (_captureRunning)
                    {
                        _worker.MessageAvailable -= OnMessageAvailable;
                        _captureRunning = false;
                    }

                    StopWorkerIfIdle();
                }
            }
            catch (PcanBasicException e)
            {
            }
        }

        public static void RegisterMessageListener(Action<Nmea2000Record> listener)
        {
            lock (SessionSyncRoot)
            {
                if (!_messageListeners.Contains(listener))
                {
                    _messageListeners.Add(listener);
                }
            }
        }

        public static void UnregisterMessageListener(Action<Nmea2000Record> listener)
        {
            lock (SessionSyncRoot)
            {
                _messageListeners.Remove(listener);
            }
        }

        public static List<Nmea2000Record> LoadCapture()
        {
            return (capture);
        }

        public static int GetCapturedCount()
        {
            return Volatile.Read(ref capturedCount);
        }

        public static bool RequestProductInformationBroadcast(out string errorMessage)
        {
            return RequestProductInformation(BroadcastAddress, out errorMessage);
        }

        public static bool RequestProductInformation(byte destinationAddress, out string errorMessage)
        {
            try
            {
                if (!BeginTransmitSession(out errorMessage))
                {
                    return false;
                }

                var requestCanId = CanBusUtilities.BuildCanId(IsoRequestPgn, destinationAddress, RequestSourceAddress, RequestPriority);
                var requestedPgnBytes = new byte[]
                {
                    (byte)(ProductInformationPgn & 0xFF),
                    (byte)((ProductInformationPgn >> 8) & 0xFF),
                    (byte)((ProductInformationPgn >> 16) & 0xFF)
                };

                var message = new PcanMessage(
                    requestCanId,
                    MessageType.Extended,
                    (byte)requestedPgnBytes.Length,
                    requestedPgnBytes,
                    false);

                if (_worker.Transmit(message, out var error))
                {
                    PcanTraceLog.LogOutgoing(requestCanId, requestedPgnBytes);
                    errorMessage = string.Empty;
                    EndTransmitSession();
                    return true;
                }

                errorMessage = $"Failed to send request: {error}.";
                EndTransmitSession();
                return false;
            }
            catch (PcanBasicException ex)
            {
                errorMessage = ex.Message;
                EndTransmitSession();
                return false;
            }
            catch (DllNotFoundException ex)
            {
                errorMessage = ex.Message;
                EndTransmitSession();
                return false;
            }
        }

        public static bool BeginTransmitSession(out string errorMessage)
        {
            try
            {
                lock (SessionSyncRoot)
                {
                    EnsureWorkerStarted();
                    _transmitSessionCount++;
                }

                PcanTraceLog.LogNote($"begin transmit session count={_transmitSessionCount}");
                errorMessage = string.Empty;
                return true;
            }
            catch (PcanBasicException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
            catch (DllNotFoundException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static bool BeginMonitorSession(out string errorMessage)
        {
            try
            {
                lock (SessionSyncRoot)
                {
                    EnsureWorkerStarted();
                    _monitorSessionCount++;
                }

                PcanTraceLog.LogNote($"begin monitor session count={_monitorSessionCount}");
                errorMessage = string.Empty;
                return true;
            }
            catch (PcanBasicException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
            catch (DllNotFoundException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static void EndMonitorSession()
        {
            lock (SessionSyncRoot)
            {
                if (_monitorSessionCount > 0)
                {
                    _monitorSessionCount--;
                }

                PcanTraceLog.LogNote($"end monitor session count={_monitorSessionCount}");
                StopWorkerIfIdle();
            }
        }

        public static void EndTransmitSession()
        {
            lock (SessionSyncRoot)
            {
                if (_transmitSessionCount > 0)
                {
                    _transmitSessionCount--;
                }

                PcanTraceLog.LogNote($"end transmit session count={_transmitSessionCount}");
                StopWorkerIfIdle();
            }
        }

        public static bool TryTransmit(uint canId, IReadOnlyList<byte> payload, out string errorMessage)
        {
            try
            {
                var message = new PcanMessage(
                    canId,
                    MessageType.Extended,
                    (byte)Math.Min(payload.Count, 8),
                    payload.ToArray(),
                    false);

                if (_worker.Transmit(message, out var error))
                {
                    PcanTraceLog.LogOutgoing(canId, payload.Take(8).ToArray());
                    errorMessage = string.Empty;
                    return true;
                }

                errorMessage = $"Failed to transmit frame: {error}.";
                return false;
            }
            catch (PcanBasicException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
            catch (DllNotFoundException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static bool IsDeviceUnavailableError(string? errorMessage)
        {
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                return false;
            }

            return errorMessage.Contains("The value of a handle", StringComparison.OrdinalIgnoreCase) ||
                errorMessage.Contains("PCAN-Channel", StringComparison.OrdinalIgnoreCase) ||
                errorMessage.Contains("PCAN-Hardware", StringComparison.OrdinalIgnoreCase) ||
                errorMessage.Contains("PCAN-Net", StringComparison.OrdinalIgnoreCase) ||
                errorMessage.Contains("PCAN-Client", StringComparison.OrdinalIgnoreCase);
        }

        private static void OnMessageAvailable(object? sender, MessageAvailableEventArgs e)
        {
            PcanMessage msg;
            ulong timestamp;
            while (_worker.Dequeue(out msg, out timestamp))
            {
                PcanTraceLog.LogIncoming(msg);
                uint priority = (msg.ID >> 26) & 0x7;
                uint source = msg.ID & 0xFF;
                uint pgn = (msg.ID >> 8) & 0x1FFFF;
                uint destination;
                var now = DateTimeOffset.Now;
                var pf = (pgn >> 8) & 0xFF;

                if (pf < 0xF0)                           // PDU1: low byte of the PGN field is destination
                {
                    destination = (pgn & 0xFF);          // Extract destination (last byte of PGN)
                    pgn &= 0x1FF00;                      // Remove destination from PGN
                }
                else
                {
                    destination = 255;                   // Broadcast for PDU2 (no destination-specific PGN)
                }

                // Format data bytes as a space-separated hex string
                byte[] data = msg.Data;
                string dataHex = string.Join(" ", data.Select(b => $"0x{b:X2}"));

                var record = new Nmea2000Record
                {
                    Timestamp = now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                    Source = source.ToString(),
                    Destination = destination.ToString(),
                    PGN = pgn.ToString(),
                    Priority = priority.ToString(),
                    Data = dataHex,
                };

                Action<Nmea2000Record>[] listeners;
                lock (SessionSyncRoot)
                {
                    listeners = _messageListeners.ToArray();
                    if (_captureRunning && capture != null)
                    {
                        capture.Add(record);
                        Interlocked.Increment(ref capturedCount);
                    }
                }

                foreach (var listener in listeners)
                {
                    try
                    {
                        listener(record);
                    }
                    catch
                    {
                    }
                }
                // Debug.WriteLine($"TS: {timestamp}, Src: {source}, Len: {msg.DLC}, PGN: {pgn}");
            }
        }

        private static void EnsureWorkerStarted()
        {
            if (_workerStarted)
            {
                return;
            }

            _worker.MessageAvailable -= OnMessageAvailable;
            _worker.MessageAvailable += OnMessageAvailable;
            _worker.Start();
            _workerStarted = true;
            PcanTraceLog.LogNote("worker started");
        }

        private static void StopWorkerIfIdle()
        {
            if (_workerStarted && !_captureRunning && _transmitSessionCount == 0 && _monitorSessionCount == 0)
            {
                _worker.Stop();
                _workerStarted = false;
                PcanTraceLog.LogNote("worker stopped");
            }
        }

    }
}
