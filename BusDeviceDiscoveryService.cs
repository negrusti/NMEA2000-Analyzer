using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using static NMEA2000Analyzer.MainWindow;
using static NMEA2000Analyzer.PgnDefinitions;

namespace NMEA2000Analyzer
{
    internal static class BusDeviceDiscoveryService
    {
        private static readonly TimeSpan ScanDuration = TimeSpan.FromSeconds(5);

        public static async Task<IReadOnlyList<DeviceBusAddressOption>> DiscoverAsync(
            IProgress<FileLoadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ScanDuration);
            var effectiveCancellationToken = timeoutCts.Token;

            var syncRoot = new object();
            var observedAddresses = new HashSet<byte>();
            var rawRecords = new List<Nmea2000Record>();
            var sequenceNumber = 0;

            void Report(string message)
            {
                progress?.Report(new FileLoadProgress
                {
                    Stage = "Scanning Bus",
                    Message = message,
                    Percent = null
                });
            }

            void OnMessageReceived(Nmea2000Record record)
            {
                var sourceAddress = CanBusUtilities.ParseAddressOrDefault(record.Source, 255);
                if (sourceAddress == 255)
                {
                    return;
                }

                lock (syncRoot)
                {
                    observedAddresses.Add(sourceAddress);
                    if (record.PGN is "60928" or "126996")
                    {
                        rawRecords.Add(CloneRecord(record, sequenceNumber++));
                    }
                }
            }

            Report("Listening for live devices and requesting product information...");
            PcanTraceLog.BeginScope("bus discovery");

            if (!PCAN.BeginMonitorSession(out var errorMessage))
            {
                PcanTraceLog.EndScope("bus discovery");
                throw new InvalidOperationException(errorMessage);
            }

            PCAN.RegisterMessageListener(OnMessageReceived);
            try
            {
                PCAN.RequestProductInformationBroadcast(out _);
                await Task.Delay(TimeSpan.FromSeconds(1), effectiveCancellationToken).ConfigureAwait(false);
                PCAN.RequestProductInformationBroadcast(out _);
                await Task.Delay(Timeout.InfiniteTimeSpan, effectiveCancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                PCAN.UnregisterMessageListener(OnMessageReceived);
                PCAN.EndMonitorSession();
                PcanTraceLog.EndScope("bus discovery");
            }

            List<Nmea2000Record> assembledRecords;
            HashSet<byte> addressSnapshot;
            lock (syncRoot)
            {
                assembledRecords = MainWindow.AssembleFrames(rawRecords.OrderBy(record => record.LogSequenceNumber).ToList());
                addressSnapshot = observedAddresses.ToHashSet();
            }

            var discoveredDevices = BuildDeviceMap(assembledRecords);
            return addressSnapshot
                .OrderBy(address => address)
                .Select(address => new DeviceBusAddressOption
                {
                    AddressText = address.ToString(CultureInfo.InvariantCulture),
                    Label = BuildAddressLabel(address, discoveredDevices)
                })
                .ToList();
        }

        private static Dictionary<byte, Device> BuildDeviceMap(IEnumerable<Nmea2000Record> assembledRecords)
        {
            var devices = new Dictionary<byte, Device>();
            var canboatRoot = ((App)Application.Current).CanboatRoot;
            if (canboatRoot == null)
            {
                return devices;
            }

            foreach (var record in assembledRecords.Where(record => record.PGN is "60928" or "126996"))
            {
                var pgnDefinition = canboatRoot.PGNs.FirstOrDefault(candidate =>
                    string.Equals(candidate.PGN.ToString(CultureInfo.InvariantCulture), record.PGN, StringComparison.Ordinal));
                if (pgnDefinition == null)
                {
                    continue;
                }

                var decoded = DecodePgnData(record.PayloadBytes, pgnDefinition);
                if (decoded?["Fields"] is not JsonArray fieldsArray)
                {
                    continue;
                }

                var address = CanBusUtilities.ParseAddressOrDefault(record.Source, 255);
                if (address == 255)
                {
                    continue;
                }

                if (!devices.TryGetValue(address, out var device))
                {
                    device = new Device { Address = address };
                    devices[address] = device;
                }

                var fields = fieldsArray.OfType<JsonObject>().ToList();
                if (record.PGN == "60928")
                {
                    device.MfgCode = GetFieldString(fields, "Manufacturer Code") ?? device.MfgCode;
                    device.DeviceClass = GetFieldString(fields, "Device Class") ?? device.DeviceClass;
                    device.DeviceFunction = GetFieldString(fields, "Device Function") ?? device.DeviceFunction;
                }
                else
                {
                    device.MfgCode = GetFieldString(fields, "Manufacturer Code") ?? device.MfgCode;
                    device.ModelID = GetFieldString(fields, "Model ID") ?? device.ModelID;
                    device.ModelVersion = GetFieldString(fields, "Model Version") ?? device.ModelVersion;
                    device.ModelSerialCode = GetFieldString(fields, "Model Serial Code") ?? device.ModelSerialCode;
                    device.SoftwareVersionCode = GetFieldString(fields, "Software Version Code") ?? device.SoftwareVersionCode;
                }
            }

            return devices;
        }

        private static string BuildAddressLabel(byte address, IReadOnlyDictionary<byte, Device> devices)
        {
            if (devices.TryGetValue(address, out var device))
            {
                var label = string.Join(" ", new[]
                {
                    device.MfgCode,
                    device.ModelID,
                    device.ModelVersion
                }.Where(value => !string.IsNullOrWhiteSpace(value)));

                if (!string.IsNullOrWhiteSpace(label))
                {
                    return $"{address} - {label}";
                }
            }

            return $"{address} - Addr {address}";
        }

        private static string? GetFieldString(IEnumerable<JsonObject> fields, string fieldName)
        {
            var value = fields.FirstOrDefault(field => field.ContainsKey(fieldName))?[fieldName];
            return value?.GetValue<object>()?.ToString();
        }

        private static Nmea2000Record CloneRecord(Nmea2000Record record, int sequenceNumber)
        {
            return new Nmea2000Record
            {
                Timestamp = record.Timestamp,
                Source = record.Source,
                Destination = record.Destination,
                PGN = record.PGN,
                Priority = record.Priority,
                PayloadBytes = record.PayloadBytes.ToArray(),
                Type = record.Type,
                Description = record.Description,
                LogSequenceNumber = sequenceNumber
            };
        }
    }
}
