using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using static NMEA2000Analyzer.PgnDefinitions;
using Application = System.Windows.Application;

namespace NMEA2000Analyzer
{
    public partial class MainWindow : Window
    {

        private List<Nmea2000Record>? _Data;
        private List<Nmea2000Record>? _assembledData;
        private ICollectionView? _dataGridView;
        private PacketViewMode _currentPacketView = PacketViewMode.Assembled;
        private readonly DispatcherTimer _filterDebounceTimer;
        private readonly HashSet<string> _distinctDataSeen = new HashSet<string>();
        private HashSet<string> _includePGNs = new HashSet<string>();
        private HashSet<string> _includeAddresses = new HashSet<string>();
        private HashSet<string> _excludePGNs = new HashSet<string>();
        private bool _distinctFilterEnabled;

        private enum PacketViewMode
        {
            Assembled,
            Unassembled
        }
        
        public MainWindow()
        {
            InitializeComponent();
            _filterDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _filterDebounceTimer.Tick += FilterDebounceTimer_Tick;
            Loaded += (s, e) => {
                PopulatePresetsMenu();
                PopulateRecentFilesMenu();
            };
            this.Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Call your async method here
            ((App)Application.Current).CanboatRoot = await LoadPgnDefinitionsAsync();
        }

        // Class to hold parsed data for the DataGrid
        public class Nmea2000Record
        {
            private string _data = string.Empty;
            private string? _asciiData;

            public int LogSequenceNumber { get; set; }
            public string? Timestamp { get; set; }
            public string Source { get; set; }
            public string Destination { get; set; }
            public string PGN { get; set; }
            public string Type { get; set; }
            public string Priority { get; set; }
            public string Description { get; set; } = ""; // Placeholder if not provided
            public string Data
            {
                get => _data;
                set
                {
                    _data = value ?? string.Empty;
                    _asciiData = null;
                }
            }
            public string? DeviceInfo { get; set; }
            public int? PGNListIndex { get; set; }
            public string DestinationDisplay => Destination == "255" ? "Bcast" : Destination;

            public string AsciiData => _asciiData ??= ConvertHexToAscii(_data);

            private static string ConvertHexToAscii(string hexString)
            {
                if (string.IsNullOrWhiteSpace(hexString))
                {
                    return string.Empty;
                }

                var hexValues = hexString.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var asciiChars = new char[hexValues.Length];

                for (var i = 0; i < hexValues.Length; i++)
                {
                    var hex = hexValues[i].Replace("0x", "", StringComparison.OrdinalIgnoreCase);
                    var byteValue = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    asciiChars[i] = byteValue is >= 32 and <= 126 ? (char)byteValue : '.';
                }

                return new string(asciiChars);
            }
        }

        private class FastPacketMessage
        {
            public int TotalBytes { get; set; }
            public int ReceivedByteCount { get; set; }
            public Dictionary<int, string[]> Frames { get; set; } = new Dictionary<int, string[]>();
            public string? Timestamp { get; set; }
            public string Source { get; set; }
            public string Destination { get; set; }
            public string PGN { get; set; }
            public string Priority { get; set; }
        }

        public class Preset
        {
            public string Name { get; set; }
            public string IncludePGNs { get; set; }
            public string ExcludePGNs { get; set; }
        }

        public class Device
        {
            public int Address { get; set; }
            public double? ProductCode { get; set; }            // From Product Information PGN
            public string? ModelID { get; set; }                // From Product Information PGN
            public string? SoftwareVersionCode { get; set; }    // From Product Information PGN
            public string? ModelVersion { get; set; }           // From Product Information PGN
            public string? ModelSerialCode { get; set; }        // From Product Information PGN
            public string? MfgCode { get; set; }                // From Address Claim PGN
            public string? DeviceClass { get; set; }            // From Address Claim PGN
            public string? DeviceFunction { get; set; }         // From Address Claim PGN


        }


        public Task LoadFileFromCommandLineAsync(string filePath)
        {
            return LoadFileAsync(filePath);
        }

        private async Task LoadFileAsync(string filePath)
        {
            ClearData();

            var format = FileFormats.DetectFileFormat(filePath);
            Title = $"NMEA2000 Analyzer - {Path.GetFileName(filePath)} " +
                    $"({Enum.GetName(typeof(FileFormats.FileFormat), format)})";

            try
            {
                switch (format)
                {
                    case FileFormats.FileFormat.TwoCanCsv:
                        _Data = await Task.Run(() => FileFormats.LoadTwoCanCsv(filePath));
                        break;
                    case FileFormats.FileFormat.Actisense:
                        _Data = await Task.Run(() => FileFormats.LoadActisense(filePath));
                        break;
                    case FileFormats.FileFormat.CanDump1:
                        _Data = await Task.Run(() => FileFormats.LoadCanDump1(filePath));
                        break;
                    case FileFormats.FileFormat.CanDump2:
                        _Data = await Task.Run(() => FileFormats.LoadCanDump2(filePath));
                        break;
                    case FileFormats.FileFormat.YDWG:
                        _Data = await Task.Run(() => FileFormats.LoadYDWGLog(filePath));
                        break;
                    case FileFormats.FileFormat.PCANView:
                        _Data = await Task.Run(() => FileFormats.LoadPCANView(filePath));
                        break;
                    case FileFormats.FileFormat.YDCsv:
                        _Data = await Task.Run(() => FileFormats.LoadYDCsv(filePath));
                        break;
                    default:
                        MessageBox.Show("Unsupported or unknown file format.", "Error",
                                        MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                }

                UpdateTimestampRange();
                EnrichUnassembledRecords(_Data);

                _assembledData = AssembleFrames(_Data);

                GenerateDeviceInfo(_assembledData);
                UpdateSrcDevices(_Data);
                UpdateSrcDevices(_assembledData);
                RefreshGridView();

                RecentFilesManager.RegisterFileOpen(filePath);
                PopulateRecentFilesMenu();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private async void OpenMenuItem_ClickAsync(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "(*.csv, *.log, *.txt, *.dump, *.trc)|*.csv;*.log;*.txt;*.dump;*.trc|All Files (*.*)|*.*",
                Title = "Open File"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string filePath = openFileDialog.FileName;
                await LoadFileAsync(filePath);
            }
        }

        private void SaveAsCandumpMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_Data == null || _Data.Count == 0)
            {
                MessageBox.Show("No unassembled data available to export.", "Save", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var saveFileDialog = new SaveFileDialog
            {
                Filter = "candump files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = ".log",
                FileName = "export.log",
                Title = "Save"
            };

            if (saveFileDialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                using var writer = new StreamWriter(saveFileDialog.FileName);

                for (var exportIndex = 0; exportIndex < _Data.Count; exportIndex++)
                {
                    var record = _Data[exportIndex];
                    var data = ParseRecordData(record.Data);
                    if (data.Length == 0)
                    {
                        continue;
                    }

                    var timestamp = GetCandumpTimestamp(record, exportIndex);
                    var canId = BuildCanId(record);
                    writer.WriteLine(FormatCandumpLine(timestamp, canId, data));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save candump file: {ex.Message}", "Save", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PopulateRecentFilesMenu()
        {
            var recentFiles = RecentFilesManager.Load();

            // Find the index of the anchor separator
            int sepIndex = FileMenu.Items.IndexOf(RecentFilesSeparator);
            if (sepIndex < 0)
                return; // nothing to do if separator not found

            // Remove any items AFTER the separator (previous recent items)
            for (int i = FileMenu.Items.Count - 1; i > sepIndex; i--)
            {
                FileMenu.Items.RemoveAt(i);
            }

            if (recentFiles.Count == 0)
            {
                // No recent files → hide the separator
                RecentFilesSeparator.Visibility = Visibility.Collapsed;
                return;
            }

            // We have recent files → ensure separator is visible
            RecentFilesSeparator.Visibility = Visibility.Visible;

            foreach (var entry in recentFiles)
            {
                var mi = new MenuItem
                {
                    Header = System.IO.Path.GetFileName(entry.FilePath),
                    Tag = entry.FilePath,
                    ToolTip = entry.FilePath
                };
                mi.Click += RecentFileMenuItem_Click;
                FileMenu.Items.Add(mi);
            }
        }

        private async void RecentFileMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is string path)
            {
                await LoadFileAsync(path);
            }
        }

        private async void RecordMenuItem_ClickAsync(object sender, RoutedEventArgs e)
        {
            if (PCAN.StartCapture())
            {
                ClearData();

                // Pause here to allow packet to capture.
                System.Windows.MessageBox.Show(
                                "Capture Started.  Press OK to stop",
                                "Capturing Data...",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information
                                );

                PCAN.StopCapture();
                
                _Data = PCAN.LoadCapture();
                UpdateTimestampRange();
                EnrichUnassembledRecords(_Data);

                _assembledData = AssembleFrames(_Data);

                GenerateDeviceInfo(_assembledData);
                UpdateSrcDevices(_Data);
                UpdateSrcDevices(_assembledData);
                RefreshGridView();

            }
            else 
            {
                System.Windows.MessageBox.Show(
                                "PCAN dongle not plugged in or driver not installed",
                                "Error:",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error
                                );
            }

        }

        private void FilterTextBoxes_TextChanged(object sender, TextChangedEventArgs e)
        {
            ScheduleFilterRefresh();
        }

        private void FilterDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _filterDebounceTimer.Stop();
            RefreshFilterView();
        }

        private void ScheduleFilterRefresh()
        {
            _filterDebounceTimer.Stop();
            _filterDebounceTimer.Start();
        }

        private void RefreshFilterView()
        {
            var sourceData = GetActiveBaseData();
            if (sourceData == null)
            {
                return;
            }

            if (!ReferenceEquals(DataGrid.ItemsSource, sourceData))
            {
                DataGrid.ItemsSource = sourceData;
                _dataGridView = CollectionViewSource.GetDefaultView(sourceData);
                if (_dataGridView != null)
                {
                    _dataGridView.Filter = FilterRecord;
                }
            }

            if (_dataGridView == null)
            {
                return;
            }

            _includePGNs = ParseList(IncludePGNTextBox.Text);
            _includeAddresses = ParseList(IncludeAddressTextBox.Text);
            _excludePGNs = ParseList(ExcludePGNTextBox.Text);
            _distinctFilterEnabled = DistinctFilterCheckBox.IsChecked == true;
            _distinctDataSeen.Clear();
            _dataGridView.Refresh();
        }

        private bool FilterRecord(object obj)
        {
            if (obj is not Nmea2000Record record)
            {
                return false;
            }

            var includePGN = _includePGNs.Count == 0 || _includePGNs.Contains(record.PGN);
            var includeSource = _includeAddresses.Count == 0
                || _includeAddresses.Contains(record.Source)
                || _includeAddresses.Contains(record.Destination);
            var exclude = _excludePGNs.Contains(record.PGN);

            if (!includePGN || !includeSource || exclude)
            {
                return false;
            }

            if (_distinctFilterEnabled)
            {
                return _distinctDataSeen.Add(record.Data);
            }

            return true;
        }

        private HashSet<string> ParseList(string input)
        {
            // Split comma-separated PGNs and normalize (trim whitespace)
            return input.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(pgn => pgn.Trim())
                        .ToHashSet();
        }
        
        private List<Nmea2000Record> AssembleFrames(List<Nmea2000Record> records)
        {
            var assembledRecords = new List<Nmea2000Record>();
            var activeMessages = new Dictionary<string, FastPacketMessage>(); // Track in-progress multi-frame messages

            int RowNumber = 0;
            foreach (var record in records)
            {
                int pgn = Convert.ToInt32(record.PGN);

                if (Globals.UniquePGNs.TryGetValue(pgn, out Canboat.Pgn pgnDefinition))
                {
                    if (pgnDefinition.Type == "Fast")
                    {
                        var messageKey = $"{record.Source}-{record.Destination}-{record.PGN}";
                        var assembled = ProcessFastPacketFrame(messageKey, record, activeMessages, assembledRecords);

                        if (assembled != null)
                        {
                            assembled.LogSequenceNumber = RowNumber;
                            byte[] byteArray = assembled.Data
                                .Split(' ')                         // Split by spaces
                                .Select(hex => Convert.ToByte(hex, 16)) // Convert each "0xXX" to a byte
                                .ToArray();

                            int? defIndex = MatchDataAgainstLookup(byteArray, pgn);
                            if (defIndex != null)
                            {
                                assembled.Description = ((App)Application.Current).CanboatRoot.PGNs[defIndex ?? 0].Description;
                                assembled.PGNListIndex = defIndex;
                            }
                            else if (Globals.PGNListLookup.Contains(pgn))
                            {
                                assembled.Description = "No pattern match";
                            }
                            else
                            {
                                assembled.Description = pgnDefinition.Description;
                            }
                            assembledRecords.Add(assembled);
                            RowNumber++;
                        }
                    }
                    else
                    {
                        byte[] byteArray = record.Data
                            .Split(' ')                         // Split by spaces
                            .Select(hex => Convert.ToByte(hex, 16)) // Convert each "0xXX" to a byte
                            .ToArray();

                        int? defIndex = MatchDataAgainstLookup(byteArray, pgn);
                        if (defIndex != null)
                        {
                            record.Description = ((App)Application.Current).CanboatRoot.PGNs[defIndex ?? 0].Description;
                            record.PGNListIndex = defIndex;
                        }
                        else if (Globals.PGNListLookup.Contains(pgn))
                        {
                            record.Description = "No pattern match";
                        }
                        else
                        {
                            record.Description = pgnDefinition.Description;
                        }

                        record.LogSequenceNumber = RowNumber;
                        record.Type = pgnDefinition.Type;
                        assembledRecords.Add(record);
                        RowNumber++;
                    }
                }
                else
                {
                    // Unknown PGN - treat as single-frame
                    record.LogSequenceNumber = RowNumber;
                    record.Description = "Unknown PGN";
                    record.Type = "Unknown";
                    assembledRecords.Add(record);
                    RowNumber++;
                }
            }

            return assembledRecords;
        }


        private static Nmea2000Record? ProcessFastPacketFrame(
            string messageKey,
            Nmea2000Record frame,
            Dictionary<string, FastPacketMessage> activeMessages,
            List<Nmea2000Record> assembledRecords)
        {
            var frameData = frame.Data.Split(' ').ToArray();

            int sequenceNumber = Convert.ToByte(frameData[0], 16) & 0x1F;

            // Check if this is the start of a new multi-frame message
            if (!activeMessages.ContainsKey(messageKey))
            {
                if (sequenceNumber == 0)
                {
                    // First frame: Extract TotalBytes from the second byte (ISO-TP header)
                    int totalBytes = Convert.ToByte(frameData[1], 16);

                    if (totalBytes == 0)
                    {
                        Debug.WriteLine($"Invalid TotalBytes for {messageKey}. Skipping.");
                        return null; // Skip invalid messages
                    }

                    activeMessages[messageKey] = new FastPacketMessage
                    {
                        TotalBytes = totalBytes,
                        Frames = new Dictionary<int, string[]>(),
                        Source = frame.Source,
                        Destination = frame.Destination,
                        PGN = frame.PGN,
                        Priority = frame.Priority,
                        Timestamp = frame.Timestamp
                    };

                    // Debug.WriteLine($"Started new message for {messageKey}: TotalBytes = {totalBytes}");
                }
                else
                {
                    // If the first frame hasn't been received yet, skip this frame
                    Debug.WriteLine($"First frame missing for {messageKey}. Skipping frame {sequenceNumber}.");
                    return null;
                }
            }

            // Add or update the frame in the message
            var message = activeMessages[messageKey];
            if (!message.Frames.ContainsKey(sequenceNumber))
            {
                string[] framePayload;

                if (sequenceNumber == 0)
                {
                    // For the first frame, skip the ISO-TP header (first two bytes)
                    framePayload = frameData.Skip(2).ToArray();
                }
                else
                {
                    // For subsequent frames, skip only the first byte (ISO-TP continuation header)
                    framePayload = frameData.Skip(1).ToArray();
                }

                message.Frames[sequenceNumber] = framePayload;
                message.ReceivedByteCount += framePayload.Length;
                //Debug.WriteLine($"Added frame {sequenceNumber} to {messageKey}: {string.Join(" ", framePayload)}");
            }
            else
            {
                Debug.WriteLine($"Duplicate frame {sequenceNumber} for {messageKey}. Ignoring.");
            }

            //Debug.WriteLine($"Message {messageKey}, TotalBytes = {message.TotalBytes}");

            // Calculate the total number of bytes received so far
            int receivedBytes = message.Frames.Values.Sum(f => f.Length);

            // Check if the message is complete
            if (message.TotalBytes > 0 && receivedBytes >= message.TotalBytes)
            {
                // Assemble the full message
                var fullMessage = message.Frames.OrderBy(f => f.Key) // Order frames by sequence number
                                                .SelectMany(f => f.Value) // Concatenate all frame data
                                                .Take(message.TotalBytes) // Take only the required number of bytes
                                                .ToArray();

                //Debug.WriteLine($"Assembled message for {messageKey}: {string.Join(" ", fullMessage)}");

                // Remove the completed message from activeMessages
                activeMessages.Remove(messageKey);

                // Add the assembled message to the final records
                return new Nmea2000Record
                {
                    Source = message.Source,
                    Destination = message.Destination,
                    PGN = message.PGN,
                    Priority = message.Priority,
                    Data = string.Join(" ", fullMessage),
                    Type = "Fast",
                    Timestamp = message.Timestamp
                };
            }
            return null;
        }

        private void PgnStatisticsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_assembledData == null || !_assembledData.Any())
            {
                MessageBox.Show("No PGN data available to display statistics.", "PGN Statistics", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Group PGNs by their count and include descriptions
            var pgnCounts = _assembledData
                .GroupBy(record => record.PGN)
                .Select(group =>
                {
                    var firstRecord = group.First();
                    return new PgnStatisticsEntry
                    {
                        PGN = group.Key,
                        Count = group.Count(),
                        Description = firstRecord.Description ?? "Unknown"
                    };
                })
                .OrderByDescending(entry => entry.Count)
                .ToList();

            // Open the statistics window
            var statsWindow = new PgnStatistics(pgnCounts)
            {
                Owner = this
            };
            statsWindow.Show();
        }

        public void IncludePgns(IEnumerable<string> pgns)
        {
            var mergedPgns = ParseList(IncludePGNTextBox.Text);
            mergedPgns.UnionWith(pgns.Where(pgn => !string.IsNullOrWhiteSpace(pgn)));

            IncludePGNTextBox.Text = string.Join(", ", mergedPgns.OrderBy(pgn => pgn, StringComparer.Ordinal));
            RefreshFilterView();
        }

        public void ExcludePgns(IEnumerable<string> pgns)
        {
            var mergedPgns = ParseList(ExcludePGNTextBox.Text);
            mergedPgns.UnionWith(pgns.Where(pgn => !string.IsNullOrWhiteSpace(pgn)));

            ExcludePGNTextBox.Text = string.Join(", ", mergedPgns.OrderBy(pgn => pgn, StringComparer.Ordinal));
            RefreshFilterView();
        }

        private void DevicesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (Globals.Devices == null || !Globals.Devices.Any())
            {
                MessageBox.Show("No device data available.", "Device Statistics", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }


            // Open the statistics window
            var devicesWindow = new Devices();
            devicesWindow.Show();
        }

        private void ClearData()
        {
            // Clear data collections
            _Data?.Clear();
            _assembledData?.Clear();
            _filterDebounceTimer.Stop();
            _dataGridView = null;
            _distinctDataSeen.Clear();
            _includePGNs.Clear();
            _includeAddresses.Clear();
            _excludePGNs.Clear();
            _distinctFilterEnabled = false;
            Globals.Devices.Clear();

            // Reset the DataGrid and data view
            DataGrid.ItemsSource = null;
            JsonViewerTextBox.Text = null;

            IncludePGNTextBox.Text = string.Empty;
            ExcludePGNTextBox.Text = string.Empty;
            IncludeAddressTextBox.Text = string.Empty;
        }

        private List<Nmea2000Record>? GetActiveBaseData()
        {
            return _currentPacketView == PacketViewMode.Assembled ? _assembledData : _Data;
        }

        private static byte[] ParseRecordData(string data)
        {
            return data.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(hex => Convert.ToByte(hex, 16))
                .ToArray();
        }

        private static uint BuildCanId(Nmea2000Record record)
        {
            var priority = int.TryParse(record.Priority, out var parsedPriority) ? parsedPriority : 0;
            var pgn = int.TryParse(record.PGN, out var parsedPgn) ? parsedPgn : 0;
            var source = int.TryParse(record.Source, out var parsedSource) ? parsedSource : 0;
            var destination = int.TryParse(record.Destination, out var parsedDestination) ? parsedDestination : 255;

            if (pgn < 0xF000)
            {
                pgn |= destination & 0xFF;
            }

            return (uint)((priority << 26) | (pgn << 8) | source);
        }

        private static double GetCandumpTimestamp(Nmea2000Record record, int exportIndex)
        {
            if (!string.IsNullOrWhiteSpace(record.Timestamp))
            {
                if (double.TryParse(record.Timestamp, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    return seconds;
                }

                if (DateTimeOffset.TryParse(record.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
                {
                    return timestamp.ToUnixTimeMilliseconds() / 1000.0;
                }

                if (TimeSpan.TryParse(record.Timestamp, CultureInfo.InvariantCulture, out var timeSpan))
                {
                    return timeSpan.TotalSeconds;
                }
            }

            return exportIndex * 0.001;
        }

        private static string FormatCandumpLine(double timestamp, uint canId, IEnumerable<byte> payload)
        {
            var data = string.Concat(payload.Select(value => $"{value:X2}"));
            return $"({timestamp:F6}) can0 {canId:X8}#{data}";
        }

        private void EnrichUnassembledRecords(List<Nmea2000Record>? records)
        {
            if (records == null)
            {
                return;
            }

            var rowNumber = 0;
            foreach (var record in records)
            {
                record.LogSequenceNumber = rowNumber++;

                if (!int.TryParse(record.PGN, out var pgn) ||
                    !Globals.UniquePGNs.TryGetValue(pgn, out var pgnDefinition))
                {
                    record.Description = "Unknown PGN";
                    record.Type = "Unknown";
                    continue;
                }

                record.Type = pgnDefinition.Type;
                record.Description = pgnDefinition.Description;
                record.PGNListIndex = null;
            }
        }

        private void RefreshGridView()
        {
            RefreshFilterView();
        }

        private void SetPacketView(PacketViewMode packetViewMode)
        {
            _currentPacketView = packetViewMode;
            AssembledViewMenuItem.IsChecked = packetViewMode == PacketViewMode.Assembled;
            UnassembledViewMenuItem.IsChecked = packetViewMode == PacketViewMode.Unassembled;
            RefreshGridView();
        }

        private void AssembledViewMenuItem_Click(object sender, RoutedEventArgs e)
        {
            SetPacketView(PacketViewMode.Assembled);
        }

        private void UnassembledViewMenuItem_Click(object sender, RoutedEventArgs e)
        {
            SetPacketView(PacketViewMode.Unassembled);
        }

        private List<Preset> LoadPresets()
        {
            const string presetsFilePath = "presets.json";

            if (!File.Exists(presetsFilePath))
            {
                MessageBox.Show("Presets file not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return new List<Preset>();
            }

            try
            {
                var jsonContent = File.ReadAllText(presetsFilePath);
                return JsonSerializer.Deserialize<List<Preset>>(jsonContent);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load presets: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return new List<Preset>();
            }
        }

        private void PresetsItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem &&
                menuItem.Tag is ValueTuple<string, string> tag)
            {
                var (includePGNs, excludePGNs) = tag;

                IncludePGNTextBox.Text = includePGNs ?? string.Empty;
                ExcludePGNTextBox.Text = excludePGNs ?? string.Empty;
                RefreshFilterView();
            }
            else
            {
                Debug.WriteLine("Invalid menuItem");
            }
        }

        private void PopulatePresetsMenu()
        {
            var presets = LoadPresets();

            // Sort presets alphabetically by Name
            var sortedPresets = presets.OrderBy(p => p.Name).ToList();

            foreach (var preset in sortedPresets)
            {
                var menuItem = new MenuItem
                {
                    Header = preset.Name,
                    Tag = (preset.IncludePGNs, preset.ExcludePGNs) // Store the PGN list in the Tag property
                };
                menuItem.Click += PresetsItem_Click;
                Presets.Items.Add(menuItem); // Add the menu item to the PresetsMenu
            }
        }

        private void IncludePgnMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.CommandParameter is Nmea2000Record selectedRow)
            {
                // Set the Include Filter to the selected PGN
                IncludePGNTextBox.Text = selectedRow.PGN;

                // Apply the filter
                RefreshFilterView();
            }
        }

        private void IncludeAddressMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.CommandParameter is Nmea2000Record selectedRow)
            {
                // Set the Include Filter to the selected PGN
                IncludeAddressTextBox.Text = selectedRow.Source;

                // Apply the filter
                RefreshFilterView();
            }
        }

        private void ReferencePgnMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.CommandParameter is Nmea2000Record selectedRow)
            {
                try
                {
                    // Construct the URL
                    var url = $"https://canboat.github.io/canboat/canboat.html#pgn-{selectedRow.PGN}";

                    // Open the URL in the default browser
                    System.Diagnostics.Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open reference URL for PGN {selectedRow.PGN}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void GoogleDeviceMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.CommandParameter is Nmea2000Record selectedRow)
            {
                try
                {
                    // Construct the URL
                    var url = $"https://www.google.com/search?q={selectedRow.DeviceInfo}";

                    // Open the URL in the default browser
                    System.Diagnostics.Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open the browser: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // Timestamp range in the footer bar 
        private void UpdateTimestampRange()
        {
            if (_Data == null || !_Data.Any())
            {
                TimestampRangeText.Text = "No data loaded";
                return;
            }

            // Parse timestamps from the data
            var timestamps = _Data
                .Select(record => DateTimeOffset.TryParse(record.Timestamp, out var timestamp) ? timestamp : (DateTimeOffset?)null)
                .Where(t => t.HasValue)
                .Select(t => t.Value)
                .ToList();

            if (timestamps.Any())
            {
                var minTimestamp = timestamps.Min();
                var maxTimestamp = timestamps.Max();

                TimestampRangeText.Text = $"Timestamp Range: {minTimestamp:G} - {maxTimestamp:G}";
            }
            else
            {
                TimestampRangeText.Text = "No valid timestamps found";
            }
        }
        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var aboutWindow = new AboutWindow
            {
                Owner = this // Set the owner to the main window
            };
            aboutWindow.ShowDialog(); // Open the dialog as a modal
        }

        private void DistinctCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            RefreshFilterView();
        }

        private void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataGrid.SelectedItem is Nmea2000Record selectedRecord)
            {
                try
                {
                    // Convert the Data string to a byte array
                    var dataBytes = selectedRecord.Data.Split(' ').Select(b => Convert.ToByte(b, 16)).ToArray();

                    JsonObject decodedJson;
                    
                    // Decode the PGN data
                    if (selectedRecord.PGNListIndex.HasValue)
                    {
                        decodedJson = DecodePgnData(dataBytes, ((App)Application.Current).CanboatRoot.PGNs[selectedRecord.PGNListIndex ?? 0]);
                    }
                    else if (selectedRecord.Description == "No pattern match")
                    {
                        decodedJson = new JsonObject();
                    }
                    else
                    {
                        decodedJson = DecodePgnData(dataBytes, ((App)Application.Current).CanboatRoot.PGNs.FirstOrDefault(q => q.PGN.ToString() == selectedRecord.PGN));
                    }

                    JsonViewerTextBox.Text = FormatDecodedYaml(decodedJson);
                }
                catch (Exception ex)
                {
                    // Retrieve the stack trace and get the line number
                    var stackTrace = new System.Diagnostics.StackTrace(ex, true);
                    var frame = stackTrace.GetFrame(0); // Get the first stack frame
                    var fileName = frame?.GetFileName(); // Get the file name
                    var lineNumber = frame?.GetFileLineNumber(); // Get the line number

                    JsonViewerTextBox.Text = $"Failed to decode PGN data:\n{ex.Message}\n({fileName}:{lineNumber})";
                }
            }
        }

        private static string FormatDecodedYaml(JsonObject? decodedJson)
        {
            if (decodedJson == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();

            foreach (var property in decodedJson)
            {
                AppendYamlProperty(builder, property.Key, property.Value, 0);
            }

            return builder.ToString().TrimEnd();
        }

        private static void AppendYamlProperty(StringBuilder builder, string key, JsonNode? value, int indentLevel)
        {
            var indent = new string(' ', indentLevel * 2);

            switch (value)
            {
                case null:
                    builder.AppendLine($"{indent}{key}: null");
                    break;
                case JsonValue jsonValue:
                    builder.AppendLine($"{indent}{key}: {FormatYamlScalar(jsonValue)}");
                    break;
                case JsonObject jsonObject:
                    builder.AppendLine($"{indent}{key}:");
                    foreach (var property in jsonObject)
                    {
                        AppendYamlProperty(builder, property.Key, property.Value, indentLevel + 1);
                    }
                    break;
                case JsonArray jsonArray:
                    AppendYamlArray(builder, key, jsonArray, indentLevel);
                    break;
            }
        }

        private static void AppendYamlArray(StringBuilder builder, string key, JsonArray jsonArray, int indentLevel)
        {
            var indent = new string(' ', indentLevel * 2);

            if (jsonArray.Count == 0)
            {
                builder.AppendLine($"{indent}{key}: []");
                return;
            }

            if (TryFlattenFieldArray(jsonArray, out var flattenedLines))
            {
                builder.AppendLine($"{indent}{key}:");
                foreach (var line in flattenedLines)
                {
                    builder.AppendLine($"{indent}  {line}");
                }
                return;
            }

            builder.AppendLine($"{indent}{key}:");
            foreach (var item in jsonArray)
            {
                AppendYamlArrayItem(builder, item, indentLevel + 1);
            }
        }

        private static void AppendYamlArrayItem(StringBuilder builder, JsonNode? item, int indentLevel)
        {
            var indent = new string(' ', indentLevel * 2);

            switch (item)
            {
                case null:
                    builder.AppendLine($"{indent}- null");
                    break;
                case JsonValue jsonValue:
                    builder.AppendLine($"{indent}- {FormatYamlScalar(jsonValue)}");
                    break;
                case JsonObject jsonObject:
                    var properties = jsonObject.ToList();
                    if (properties.Count == 0)
                    {
                        builder.AppendLine($"{indent}- {{}}");
                        return;
                    }

                    builder.AppendLine($"{indent}- {properties[0].Key}: {FormatYamlNodeInline(properties[0].Value)}");
                    for (var i = 1; i < properties.Count; i++)
                    {
                        AppendYamlProperty(builder, properties[i].Key, properties[i].Value, indentLevel + 1);
                    }
                    break;
                case JsonArray jsonArray:
                    builder.AppendLine($"{indent}-");
                    foreach (var child in jsonArray)
                    {
                        AppendYamlArrayItem(builder, child, indentLevel + 1);
                    }
                    break;
            }
        }

        private static bool TryFlattenFieldArray(JsonArray jsonArray, out List<string> flattenedLines)
        {
            flattenedLines = new List<string>();

            foreach (var item in jsonArray)
            {
                if (item is not JsonObject jsonObject || jsonObject.Count != 1)
                {
                    flattenedLines.Clear();
                    return false;
                }

                var property = jsonObject.First();
                flattenedLines.Add($"{property.Key}: {FormatYamlNodeInline(property.Value)}");
            }

            return true;
        }

        private static string FormatYamlNodeInline(JsonNode? value)
        {
            return value switch
            {
                null => "null",
                JsonValue jsonValue => FormatYamlScalar(jsonValue),
                JsonObject or JsonArray => JsonSerializer.Serialize(value),
                _ => string.Empty
            };
        }

        private static string FormatYamlScalar(JsonValue value)
        {
            if (value.TryGetValue<string>(out var stringValue))
            {
                return NeedsYamlQuotes(stringValue)
                    ? $"\"{stringValue.Replace("\"", "\\\"")}\""
                    : stringValue;
            }

            return value.ToJsonString();
        }

        private static bool NeedsYamlQuotes(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }

            if (value.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return value.Any(ch => char.IsWhiteSpace(ch) || ch is ':' or '#' or '-' or '"' or '\'' or '[' or ']' or '{' or '}');
        }

        private int _currentSearchIndex = -1;

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Perform the first search
            SearchInDataGrid(forward: true);
        }

        private void SearchBackButton_Click(object sender, RoutedEventArgs e)
        {
            SearchInDataGrid(forward: false);
        }

        private void SearchForwardButton_Click(object sender, RoutedEventArgs e)
        {
            SearchInDataGrid(forward: true);
        }

        private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
        {
            IncludePGNTextBox.Text = string.Empty;
            ExcludePGNTextBox.Text = string.Empty;
            IncludeAddressTextBox.Text = string.Empty;
            DistinctFilterCheckBox.IsChecked = false;
        }

        private void SearchInDataGrid(bool forward)
        {
            if (_assembledData == null || !_assembledData.Any() ||
                !Regex.IsMatch(PgnSearchTextBox.Text ?? string.Empty, @"^\d{5,6}$"))
            {
                return;
            }

            string searchText = PgnSearchTextBox.Text.Trim();
            int startIndex = forward ? _currentSearchIndex + 1 : _currentSearchIndex - 1;

            // Wrap-around behavior
            if (startIndex >= _assembledData.Count)
                startIndex = 0;
            else if (startIndex < 0)
                startIndex = _assembledData.Count - 1;

            for (int i = 0; i < _assembledData.Count; i++)
            {
                int index = (startIndex + (forward ? i : -i + _assembledData.Count)) % _assembledData.Count;
                if (_assembledData[index].PGN != null && _assembledData[index].PGN.Contains(searchText))
                {
                    _currentSearchIndex = index;

                    // Select and scroll into view in the DataGrid
                    DataGrid.SelectedItem = _assembledData[index];
                    DataGrid.ScrollIntoView(DataGrid.SelectedItem);

                    return;
                }
            }

            // If no match found
            MessageBox.Show("No matching PGN found.", "Search Result", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Enriches DataGrid with device information if available
        public void UpdateSrcDevices(List<Nmea2000Record> data)
        {
            foreach (var record in data)
            {
                Globals.Devices.TryGetValue(Convert.ToByte(record.Source), out var result);
                if (result != null)
                {
                    // Raymarine product codes are not really informative, use ModelVersion instead
                    if (Regex.IsMatch(result.ModelID, @"^[A-Z]\d{5}$"))
                    {
                        record.DeviceInfo = result.ModelVersion;
                    }
                    else
                    {
                        record.DeviceInfo = result.ModelID;
                    }
                }
            }
        }
        private void CopyDataMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Find the DataGrid that triggered the context menu
            if (sender is MenuItem menuItem &&
                menuItem.DataContext is Nmea2000Record selectedItem)
            {
                // Get the value of the "Data" property
                string? dataValue = selectedItem.Data;

                if (!string.IsNullOrEmpty(dataValue))
                {
                    // Copy the value to the clipboard
                    Clipboard.SetText(dataValue);
                }
            }
        }

        private void CopyRowAsCsvMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is Nmea2000Record selectedItem)
            {
                var properties = typeof(Nmea2000Record).GetProperties();
                var csvValues = new List<string>
                    {
                        selectedItem.ToString() // Add the Key first
                    };

                // Add each property value from the class
                foreach (var property in properties)
                {
                    var value = property.GetValue(selectedItem)?.ToString() ?? string.Empty;
                    csvValues.Add(value);
                }

                // Create a CSV string
                string csvRow = string.Join(",", csvValues);

                // Copy the CSV string to the clipboard
                Clipboard.SetText(csvRow);
            }
        }
    }

}
