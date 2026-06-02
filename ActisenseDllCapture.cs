using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using static NMEA2000Analyzer.MainWindow;

namespace NMEA2000Analyzer
{
    internal static class ActisenseDllCapture
    {
        public const string DefaultDllPath = @"C:\Program Files\Actisense\NMEAReader\ActisenseComms_Release_x64.dll";

        private const int DefaultBaudRate = 115200;
        private const int ReceiveAllOperatingMode = 1;

        private static readonly object SyncRoot = new();

        private static CancellationTokenSource? _captureCancellation;
        private static Task? _readerTask;
        private static List<Nmea2000Record>? _capture;
        private static int _capturedCount;
        private static int _handle;
        private static PortInfo? _activePort;

        public static bool IsAvailable()
        {
            return File.Exists(DefaultDllPath);
        }

        public static string? GetActiveSourceLabel()
        {
            return _activePort?.DisplayLabel;
        }

        public static bool StartCapture(out string sourceLabel, out string errorMessage)
        {
            sourceLabel = string.Empty;
            errorMessage = string.Empty;

            if (!IsAvailable())
            {
                errorMessage = $"Actisense DLL not found at '{DefaultDllPath}'.";
                return false;
            }

            try
            {
                StopCapture();

                var ports = EnumeratePorts();
                var selectedPort = SelectPreferredPort(ports);
                if (!selectedPort.HasValue)
                {
                    errorMessage = "No Actisense device was detected by the Actisense communications library.";
                    return false;
                }

                var selected = selectedPort.Value;

                var createResult = NativeMethods.ACommsCreate(out _handle);
                if (createResult != 0 || _handle == 0)
                {
                    errorMessage = $"Actisense DLL failed to create a communications handle (error {createResult}).";
                    CleanupHandle();
                    return false;
                }

                var openResult = NativeMethods.ACommsOpen(_handle, selected.PortId, DefaultBaudRate);
                if (openResult != 0)
                {
                    errorMessage = $"Actisense DLL failed to open {selected.DisplayLabel} (error {openResult}).";
                    CleanupHandle();
                    return false;
                }

                // Best effort: diagnostic/logging tools want the NGT-1 in "Receive All Transfer" mode.
                try
                {
                    NativeMethods.ACommsCommand_SetOperatingMode(_handle, ReceiveAllOperatingMode);
                }
                catch
                {
                }

                lock (SyncRoot)
                {
                    _capture = new List<Nmea2000Record>();
                }

                _capturedCount = 0;
                _activePort = selected;
                _captureCancellation = new CancellationTokenSource();
                _readerTask = Task.Run(() => ReadLoop(_captureCancellation.Token), _captureCancellation.Token);
                sourceLabel = selected.DisplayLabel;
                return true;
            }
            catch (DllNotFoundException)
            {
                errorMessage = $"Actisense DLL not found at '{DefaultDllPath}'.";
                StopCapture();
                return false;
            }
            catch (BadImageFormatException ex)
            {
                errorMessage = $"Actisense DLL could not be loaded: {ex.Message}";
                StopCapture();
                return false;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
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

            _readerTask = null;

            _captureCancellation?.Dispose();
            _captureCancellation = null;

            try
            {
                if (_handle != 0)
                {
                    NativeMethods.ACommsClose(_handle);
                }
            }
            catch
            {
            }

            CleanupHandle();
            _activePort = null;
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

        private static void ReadLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var item = new ACommsN2KItem();
                    var result = NativeMethods.ACommsN2K_Read(_handle, out item);
                    if (result != 0)
                    {
                        Task.Delay(10, cancellationToken).Wait(cancellationToken);
                        continue;
                    }

                    AddRecord(item);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    Task.Delay(50, cancellationToken).Wait(cancellationToken);
                }
            }
        }

        private static void AddRecord(ACommsN2KItem item)
        {
            var payloadLength = (int)Math.Min(item.DataLength, (uint)item.Data.Length);
            var payloadBytes = new byte[payloadLength];
            Array.Copy(item.Data, payloadBytes, payloadLength);

            var record = new Nmea2000Record
            {
                Timestamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                Source = item.Source.ToString(CultureInfo.InvariantCulture),
                Destination = item.Destination.ToString(CultureInfo.InvariantCulture),
                PGN = item.Pgn.ToString(CultureInfo.InvariantCulture),
                Type = payloadLength > 8 ? "fast" : "single",
                Priority = item.Priority.ToString(CultureInfo.InvariantCulture),
                PayloadBytes = payloadBytes
            };

            lock (SyncRoot)
            {
                _capture ??= new List<Nmea2000Record>();
                record.LogSequenceNumber = _capture.Count + 1;
                _capture.Add(record);
            }

            Interlocked.Increment(ref _capturedCount);
        }

        private static IReadOnlyList<PortInfo> EnumeratePorts()
        {
            var scanBuffer = new uint[512];
            var result = NativeMethods.ACommsEnumerateSerialPorts(scanBuffer);
            if (result != 0)
            {
                throw new InvalidOperationException($"Actisense DLL failed to enumerate ports (error {result}).");
            }

            var count = (int)Math.Min(scanBuffer[0], 255u);
            var ports = new List<PortInfo>(count);
            for (var index = 0; index < count; index++)
            {
                var portId = (int)scanBuffer[index + 1];
                if (portId <= 0)
                {
                    continue;
                }

                var description = GetPortName(portId);
                var status = (int)scanBuffer[257 + index];
                ports.Add(new PortInfo(portId, description, status));
            }

            return ports;
        }

        private static PortInfo? SelectPreferredPort(IReadOnlyList<PortInfo> ports)
        {
            return ports
                .OrderByDescending(port => port.IsActisenseNamed)
                .ThenBy(port => port.PortId)
                .FirstOrDefault();
        }

        private static string GetPortName(int portId)
        {
            var namePointer = NativeMethods.ACommsEnumerateSerialPortsGetName((uint)portId);
            return namePointer == IntPtr.Zero
                ? $"Port {portId}"
                : Marshal.PtrToStringAnsi(namePointer) ?? $"Port {portId}";
        }

        private static void CleanupHandle()
        {
            if (_handle == 0)
            {
                return;
            }

            try
            {
                NativeMethods.ACommsDestroy(_handle);
            }
            catch
            {
            }

            _handle = 0;
        }

        private readonly record struct PortInfo(int PortId, string Name, int Status)
        {
            public bool IsActisenseNamed => Name.IndexOf("Actisense", StringComparison.OrdinalIgnoreCase) >= 0;

            public string DisplayLabel => string.IsNullOrWhiteSpace(Name)
                ? $"Port {PortId}"
                : $"{Name} [id {PortId}]";
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct ACommsN2KItem
        {
            public uint TimestampMs;
            public uint Pgn;
            public byte Priority;
            public byte Source;
            public byte Destination;
            public byte Pad;
            public uint DataLength;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 224)]
            public byte[] Data;
        }

        private static class NativeMethods
        {
            [DllImport(DefaultDllPath, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
            public static extern int ACommsCreate(out int handle);

            [DllImport(DefaultDllPath, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
            public static extern int ACommsDestroy(int handle);

            [DllImport(DefaultDllPath, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
            public static extern int ACommsOpen(int handle, int portNumber, int baudRate);

            [DllImport(DefaultDllPath, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
            public static extern int ACommsClose(int handle);

            [DllImport(DefaultDllPath, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
            public static extern int ACommsEnumerateSerialPorts([Out] uint[] scanBuffer);

            [DllImport(DefaultDllPath, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
            public static extern IntPtr ACommsEnumerateSerialPortsGetName(uint portNumber);

            [DllImport(DefaultDllPath, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
            public static extern int ACommsN2K_Read(int handle, out ACommsN2KItem item);

            [DllImport(DefaultDllPath, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
            public static extern int ACommsCommand_SetOperatingMode(int handle, int mode);
        }
    }
}
