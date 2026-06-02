using Microsoft.Win32.SafeHandles;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using static NMEA2000Analyzer.MainWindow;

namespace NMEA2000Analyzer
{
    internal static class MastervoltHidCapture
    {
        private const ushort VendorId = 0x1A64;
        private const ushort ProductId = 0x0000;
        private const int HidPayloadSize = 64;
        private const int HidReportBufferSize = 65;
        private const int MaxPacketsPerReport = 4;
        private const int PacketSize = 14;
        private const uint IsoRequestPgn = 59904;
        private const uint ProductInformationPgn = 126996;
        private const byte BroadcastAddress = 255;
        private const byte RequestSourceAddress = 254;
        private const byte RequestPriority = 6;

        private const int DigcfPresent = 0x00000002;
        private const int DigcfDeviceInterface = 0x00000010;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;

        private static readonly object SyncRoot = new();
        private static readonly object WriteSyncRoot = new();
        private static CancellationTokenSource? _captureCancellation;
        private static Task? _readerTask;
        private static FileStream? _stream;
        private static SafeFileHandle? _deviceHandle;
        private static List<Nmea2000Record>? _capture;
        private static int _capturedCount;
        private static string? _activeDevicePath;

        public static string? GetActiveDevicePath()
        {
            return _activeDevicePath;
        }

        public static string? AutoDetectDevicePath()
        {
            return EnumerateDevicePaths().FirstOrDefault();
        }

        public static bool StartCapture(string? devicePath = null)
        {
            try
            {
                StopCapture();

                devicePath ??= AutoDetectDevicePath();
                if (string.IsNullOrWhiteSpace(devicePath))
                {
                    return false;
                }

                lock (SyncRoot)
                {
                    _capture ??= new List<Nmea2000Record>();
                    _capture.Clear();
                }

                _capturedCount = 0;
                _captureCancellation = new CancellationTokenSource();
                _deviceHandle = CreateFile(
                    devicePath,
                    GenericRead | GenericWrite,
                    FileShareRead | FileShareWrite,
                    IntPtr.Zero,
                    OpenExisting,
                    0,
                    IntPtr.Zero);

                if (_deviceHandle.IsInvalid)
                {
                    StopCapture();
                    return false;
                }

                _stream = new FileStream(_deviceHandle, FileAccess.ReadWrite, HidReportBufferSize, isAsync: false);
                _activeDevicePath = devicePath;
                _readerTask = Task.Run(() => ReadLoop(_captureCancellation.Token), _captureCancellation.Token);
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
            try
            {
                _captureCancellation?.Cancel();
            }
            catch
            {
            }

            try
            {
                _stream?.Dispose();
            }
            catch
            {
            }

            try
            {
                _deviceHandle?.Dispose();
            }
            catch
            {
            }

            try
            {
                _readerTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }

            _stream = null;
            _deviceHandle = null;
            _readerTask = null;
            _activeDevicePath = null;

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
                var requestCanId = CanBusUtilities.BuildCanId(
                    IsoRequestPgn,
                    BroadcastAddress,
                    RequestSourceAddress,
                    RequestPriority);
                var payload = new byte[]
                {
                    (byte)(ProductInformationPgn & 0xFF),
                    (byte)((ProductInformationPgn >> 8) & 0xFF),
                    (byte)((ProductInformationPgn >> 16) & 0xFF)
                };

                WriteCanFrame(requestCanId, payload);
                errorMessage = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static void ReadLoop(CancellationToken cancellationToken)
        {
            var buffer = new byte[HidReportBufferSize];

            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = _stream?.Read(buffer, 0, buffer.Length) ?? 0;
                }
                catch
                {
                    break;
                }

                if (bytesRead <= 0)
                {
                    continue;
                }

                ProcessReport(buffer, bytesRead);
            }
        }

        private static void ProcessReport(byte[] reportBuffer, int bytesRead)
        {
            var payloadOffset = GetPayloadOffset(reportBuffer, bytesRead);
            if (payloadOffset < 0)
            {
                return;
            }

            var count = reportBuffer[payloadOffset];
            if (count > MaxPacketsPerReport)
            {
                return;
            }

            for (var slot = 0; slot < count; slot++)
            {
                var packetOffset = payloadOffset + 1 + (PacketSize * slot);
                var metaLowOffset = payloadOffset + 60 + slot;
                if (packetOffset + PacketSize > bytesRead || metaLowOffset >= bytesRead)
                {
                    return;
                }

                if (TryCreateRecord(reportBuffer, packetOffset, out var record))
                {
                    lock (SyncRoot)
                    {
                        _capture ??= new List<Nmea2000Record>();
                        _capture.Add(record);
                    }

                    Interlocked.Increment(ref _capturedCount);
                }
            }
        }

        private static int GetPayloadOffset(byte[] reportBuffer, int bytesRead)
        {
            if (bytesRead >= HidReportBufferSize && reportBuffer[0] == 0 && reportBuffer[1] <= MaxPacketsPerReport)
            {
                return 1;
            }

            if (bytesRead >= HidPayloadSize && reportBuffer[0] <= MaxPacketsPerReport)
            {
                return 0;
            }

            if (bytesRead >= HidReportBufferSize && reportBuffer[1] <= MaxPacketsPerReport)
            {
                return 1;
            }

            return -1;
        }

        private static bool TryCreateRecord(byte[] reportBuffer, int packetOffset, out Nmea2000Record record)
        {
            record = null!;

            var extId = UnpackCanId(reportBuffer, packetOffset);
            var dlc = reportBuffer[packetOffset + 4] & 0x0F;
            if (dlc > 8)
            {
                return false;
            }

            var payloadBytes = new byte[dlc];
            Array.Copy(reportBuffer, packetOffset + 5, payloadBytes, 0, dlc);

            var priority = (extId >> 26) & 0x7;
            var source = extId & 0xFF;
            var pgn = (extId >> 8) & 0x3FFFF;
            var destination = 255U;
            var pf = (pgn >> 8) & 0xFF;

            if (pf < 0xF0)
            {
                destination = pgn & 0xFF;
                pgn &= 0x3FF00;
            }

            record = new Nmea2000Record
            {
                Timestamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                Source = source.ToString(CultureInfo.InvariantCulture),
                Destination = destination.ToString(CultureInfo.InvariantCulture),
                PGN = pgn.ToString(CultureInfo.InvariantCulture),
                Priority = priority.ToString(CultureInfo.InvariantCulture),
                PayloadBytes = payloadBytes,
                Description = "Unknown PGN",
                Type = "Unknown"
            };

            return true;
        }

        private static uint UnpackCanId(byte[] packet, int offset)
        {
            var b0 = packet[offset];
            var b1 = packet[offset + 1];
            var b2 = packet[offset + 2];
            var b3 = packet[offset + 3];
            var top5 = (uint)(((((ushort)b0 << 8) | b1) >> 5) & 0x1F);
            var low18 = (uint)((b1 & 0x03) << 16) | ((uint)b2 << 8) | b3;
            var low23 = (top5 << 18) | low18;
            return ((uint)(b0 >> 2) << 23) | low23;
        }

        private static void WriteCanFrame(uint extId, IReadOnlyList<byte> payload)
        {
            if (_stream == null)
            {
                throw new InvalidOperationException("Mastervolt HID gateway is not open.");
            }

            if (payload.Count > 8)
            {
                throw new ArgumentOutOfRangeException(nameof(payload), "CAN payloads cannot exceed 8 bytes.");
            }

            var report = new byte[HidReportBufferSize];
            var payloadOffset = 1;
            report[payloadOffset] = 1;
            PackCanFrame(report, payloadOffset + 1, extId, payload);

            lock (WriteSyncRoot)
            {
                _stream.Write(report, 0, report.Length);
                _stream.Flush();
            }
        }

        private static void PackCanFrame(byte[] report, int packetOffset, uint extId, IReadOnlyList<byte> payload)
        {
            var type = (byte)((extId >> 23) & 0x3F);
            var low23 = extId & 0x7FFFFF;

            report[packetOffset] = (byte)(type << 2);
            PackLow23(report, packetOffset, low23);
            report[packetOffset + 4] = (byte)(payload.Count & 0x0F);

            for (var i = 0; i < payload.Count; i++)
            {
                report[packetOffset + 5 + i] = payload[i];
            }
        }

        private static void PackLow23(byte[] report, int packetOffset, uint low23)
        {
            report[packetOffset] = (byte)((uint)(report[packetOffset] & 0xFC) | ((low23 >> 21) & 0x03));
            report[packetOffset + 1] = (byte)(((low23 >> 13) & 0xE0) | (uint)(report[packetOffset + 1] & 0x1C) | ((low23 >> 16) & 0x03));
            report[packetOffset + 2] = (byte)((low23 >> 8) & 0xFF);
            report[packetOffset + 3] = (byte)(low23 & 0xFF);
        }

        private static IReadOnlyList<string> EnumerateDevicePaths()
        {
            HidD_GetHidGuid(out var hidGuid);
            var deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
            {
                return Array.Empty<string>();
            }

            try
            {
                var paths = new List<string>();
                var interfaceData = new SpDeviceInterfaceData
                {
                    cbSize = Marshal.SizeOf<SpDeviceInterfaceData>()
                };

                for (uint index = 0; SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData); index++)
                {
                    SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
                    if (requiredSize <= 0)
                    {
                        continue;
                    }

                    var detailDataBuffer = Marshal.AllocHGlobal(requiredSize);
                    try
                    {
                        Marshal.WriteInt32(detailDataBuffer, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailDataBuffer, requiredSize, out _, IntPtr.Zero))
                        {
                            continue;
                        }

                        var pathPointer = IntPtr.Add(detailDataBuffer, 4);
                        var devicePath = Marshal.PtrToStringAuto(pathPointer);
                        if (!string.IsNullOrWhiteSpace(devicePath) &&
                            devicePath.Contains($"vid_{VendorId:x4}", StringComparison.OrdinalIgnoreCase) &&
                            devicePath.Contains($"pid_{ProductId:x4}", StringComparison.OrdinalIgnoreCase))
                        {
                            paths.Add(devicePath);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detailDataBuffer);
                    }
                }

                return paths;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid,
            string? enumerator,
            IntPtr hwndParent,
            int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet,
            IntPtr deviceInfoData,
            ref Guid interfaceClassGuid,
            uint memberIndex,
            ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet,
            ref SpDeviceInterfaceData deviceInterfaceData,
            IntPtr deviceInterfaceDetailData,
            int deviceInterfaceDetailDataSize,
            out int requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDeviceInterfaceData
        {
            public int cbSize;
            public Guid interfaceClassGuid;
            public int flags;
            public IntPtr reserved;
        }
    }
}
