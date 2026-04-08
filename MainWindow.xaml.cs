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
using System.Windows.Media;
using static NMEA2000Analyzer.PgnDefinitions;
using Application = System.Windows.Application;
using LiveChartsCore.Defaults;

namespace NMEA2000Analyzer
{
    public partial class MainWindow : Window
    {
        private const long ProgressDialogThresholdBytes = 32L * 1024 * 1024;

        private List<Nmea2000Record>? _Data;
        private List<Nmea2000Record>? _assembledData;
        private ICollectionView? _dataGridView;
        private PacketViewMode _currentPacketView = PacketViewMode.Assembled;
        private readonly DispatcherTimer _filterDebounceTimer;
        private readonly HashSet<string> _distinctDataSeen = new HashSet<string>();
        private HashSet<string> _includePGNs = new HashSet<string>();
        private HashSet<string> _includeAddresses = new HashSet<string>();
        private HashSet<string> _excludePGNs = new HashSet<string>();
        private HashSet<Nmea2000Record>? _visibleRecords;
        private List<Nmea2000Record>? _indexedDataSource;
        private FilterIndexes? _filterIndexes;
        private bool _distinctFilterEnabled;
        private readonly Dictionary<string, Brush> _highlightBackgroundsByPgn = new(StringComparer.Ordinal);
        private static readonly HashSet<string> AlarmHistoryPgns = new(StringComparer.Ordinal)
        {
            "65288",
            "65361",
            "126983",
            "126984",
            "126985",
            "126986",
            "126987",
            "126988",
            "130850",
            "130856"
        };

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
            LoadHighlights();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (((App)Application.Current).CanboatRoot == null)
            {
                ((App)Application.Current).CanboatRoot = await LoadPgnDefinitionsAsync();
            }
        }

        // Class to hold parsed data for the DataGrid
        public class Nmea2000Record
        {
            private byte[] _payloadBytes = Array.Empty<byte>();
            private string? _data;
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
                get => _data ??= FormatPayloadBytes(_payloadBytes);
                set
                {
                    _payloadBytes = ParseHexData(value);
                    _data = null;
                    _asciiData = null;
                }
            }
            public byte[] PayloadBytes
            {
                get => _payloadBytes;
                set
                {
                    _payloadBytes = value ?? Array.Empty<byte>();
                    _data = null;
                    _asciiData = null;
                }
            }
            public string? DeviceInfo { get; set; }
            public int? PGNListIndex { get; set; }
            public string DestinationDisplay => Destination == "255" ? "Bcast" : Destination;
            public Brush HighlightBackground { get; set; } = Brushes.Transparent;

            public string AsciiData => _asciiData ??= ConvertBytesToAscii(_payloadBytes);

            private static string FormatPayloadBytes(byte[] payloadBytes)
            {
                return string.Join(" ", payloadBytes.Select(b => $"0x{b:X2}"));
            }

            private static byte[] ParseHexData(string? hexString)
            {
                if (string.IsNullOrWhiteSpace(hexString))
                {
                    return Array.Empty<byte>();
                }

                return hexString
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(hex => Convert.ToByte(hex, 16))
                    .ToArray();
            }

            private static string ConvertBytesToAscii(byte[] payloadBytes)
            {
                if (payloadBytes.Length == 0)
                {
                    return string.Empty;
                }

                var asciiChars = new char[payloadBytes.Length];

                for (var i = 0; i < payloadBytes.Length; i++)
                {
                    asciiChars[i] = payloadBytes[i] is >= 32 and <= 126 ? (char)payloadBytes[i] : '.';
                }

                return new string(asciiChars);
            }
        }

        private class FastPacketMessage
        {
            public int TotalBytes { get; set; }
            public int ReceivedByteCount { get; set; }
            public Dictionary<int, byte[]> Frames { get; set; } = new Dictionary<int, byte[]>();
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

        private sealed class FilterIndexes
        {
            public Dictionary<string, List<Nmea2000Record>> ByPgn { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, List<Nmea2000Record>> BySource { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, List<Nmea2000Record>> ByDestination { get; } = new(StringComparer.Ordinal);
        }

        private readonly record struct FastPacketKey(string Source, string Destination, string Pgn);

        private void SetWindowTitle(string? name = null, string? formatLabel = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                Title = "NMEA2000 Analyzer";
                return;
            }

            Title = string.IsNullOrWhiteSpace(formatLabel)
                ? $"NMEA2000 Analyzer - {name}"
                : $"NMEA2000 Analyzer - {name} ({formatLabel})";
        }


        public Task LoadFileFromCommandLineAsync(string filePath)
        {
            return LoadFileAsync(filePath);
        }

        public Task LoadFileFromMcpAsync(string filePath)
        {
            return LoadFileAsync(filePath);
        }

        public JsonObject HighlightPacketsFromMcp(IEnumerable<int> sequences, bool assembled)
        {
            var requestedSequences = sequences
                .Distinct()
                .ToList();

            if (requestedSequences.Count == 0)
            {
                return new JsonObject
                {
                    ["assembled"] = assembled,
                    ["requestedCount"] = 0,
                    ["matchedCount"] = 0
                };
            }

            SetPacketView(assembled ? PacketViewMode.Assembled : PacketViewMode.Unassembled);

            var sourceData = GetCurrentSourceData();
            if (sourceData == null || sourceData.Count == 0)
            {
                return new JsonObject
                {
                    ["assembled"] = assembled,
                    ["requestedCount"] = requestedSequences.Count,
                    ["matchedCount"] = 0,
                    ["missingSeqs"] = new JsonArray(requestedSequences.Select(value => JsonValue.Create(value)).ToArray())
                };
            }

            DataGrid.UnselectAll();

            var matchedRecords = sourceData
                .Where(record => requestedSequences.Contains(record.LogSequenceNumber))
                .OrderBy(record => requestedSequences.IndexOf(record.LogSequenceNumber))
                .ToList();

            foreach (var record in matchedRecords)
            {
                DataGrid.SelectedItems.Add(record);
            }

            if (matchedRecords.Count > 0)
            {
                var firstRecord = matchedRecords[0];
                DataGrid.SelectedItem = firstRecord;

                if (DataGrid.Columns.Count > 0)
                {
                    DataGrid.CurrentCell = new DataGridCellInfo(firstRecord, DataGrid.Columns[0]);
                }

                DataGrid.ScrollIntoView(firstRecord);
                DataGrid.Focus();
                Activate();
            }

            var matchedSeqs = matchedRecords
                .Select(record => record.LogSequenceNumber)
                .ToHashSet();

            var missingSeqs = requestedSequences
                .Where(seq => !matchedSeqs.Contains(seq))
                .ToList();

            return new JsonObject
            {
                ["assembled"] = assembled,
                ["requestedCount"] = requestedSequences.Count,
                ["matchedCount"] = matchedRecords.Count,
                ["selectedSeqs"] = new JsonArray(matchedRecords.Select(record => JsonValue.Create(record.LogSequenceNumber)).ToArray()),
                ["missingSeqs"] = new JsonArray(missingSeqs.Select(value => JsonValue.Create(value)).ToArray())
            };
        }

        public List<Nmea2000Record> GetSelectedPacketsForMcp()
        {
            return DataGrid.SelectedItems
                .OfType<Nmea2000Record>()
                .ToList();
        }

        public Nmea2000Record? GetCurrentPacketForMcp()
        {
            return DataGrid.SelectedItem as Nmea2000Record;
        }

        public JsonObject SetPgnFiltersFromMcp(IEnumerable<string> includePgns, IEnumerable<string> excludePgns)
        {
            IncludePGNTextBox.Text = string.Join(", ",
                includePgns
                    .Where(pgn => !string.IsNullOrWhiteSpace(pgn))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(pgn => pgn, StringComparer.Ordinal));

            ExcludePGNTextBox.Text = string.Join(", ",
                excludePgns
                    .Where(pgn => !string.IsNullOrWhiteSpace(pgn))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(pgn => pgn, StringComparer.Ordinal));

            RefreshFilterView();

            return new JsonObject
            {
                ["includePgns"] = IncludePGNTextBox.Text,
                ["excludePgns"] = ExcludePGNTextBox.Text
            };
        }

        public JsonObject ClearFiltersFromMcp()
        {
            IncludePGNTextBox.Text = string.Empty;
            ExcludePGNTextBox.Text = string.Empty;
            IncludeAddressTextBox.Text = string.Empty;
            DistinctFilterCheckBox.IsChecked = false;
            RefreshFilterView();

            return new JsonObject
            {
                ["cleared"] = true
            };
        }

        private async Task LoadFileAsync(string filePath)
        {
            ClearData();
            LoadingProgressWindow? progressWindow = null;

            try
            {
                var progress = CreateLoadProgress(filePath, out progressWindow);
                var result = await CaptureLoadService.LoadAsync(filePath, progress);
                _Data = result.RawRecords;
                _assembledData = result.AssembledRecords;
                ApplyHighlights(_Data);
                ApplyHighlights(_assembledData);
                SetWindowTitle(
                    Path.GetFileName(filePath),
                    Enum.GetName(typeof(FileFormats.FileFormat), result.Format));

                ActiveDataSessionService.SetCurrent(
                    Path.GetFileName(filePath),
                    result.Format.ToString(),
                    _Data,
                    _assembledData,
                    result.FirstTimestamp,
                    result.LastTimestamp,
                    filePath);

                UpdateTimestampRange(result.FirstTimestamp, result.LastTimestamp);
                RefreshGridView();

                RecentFilesManager.RegisterFileOpen(filePath);
                PopulateRecentFilesMenu();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                progressWindow?.Close();
            }
        }

        private IProgress<FileLoadProgress>? CreateLoadProgress(string filePath, out LoadingProgressWindow? progressWindow)
        {
            progressWindow = null;

            if (!ShouldShowLoadProgress(filePath))
            {
                return null;
            }

            progressWindow = new LoadingProgressWindow
            {
                Owner = this
            };

            progressWindow.UpdateProgress(
                Path.GetFileName(filePath),
                new FileLoadProgress
                {
                    Stage = "Preparing",
                    Message = "Starting load...",
                    Percent = 0
                });
            progressWindow.Show();

            var window = progressWindow;
            return new Progress<FileLoadProgress>(progress =>
                window.UpdateProgress(Path.GetFileName(filePath), progress));
        }

        private static bool ShouldShowLoadProgress(string filePath)
        {
            try
            {
                return new FileInfo(filePath).Length >= ProgressDialogThresholdBytes;
            }
            catch
            {
                return false;
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
                    var data = ParseRecordData(record);
                    if (data.Length == 0)
                    {
                        continue;
                    }

                    var timestamp = GetCandumpTimestamp(record, exportIndex);
                    var canId = BuildCanId(record);
                    writer.WriteLine(FormatCandumpLine(timestamp, canId, data));
                }

                SetWindowTitle(Path.GetFileName(saveFileDialog.FileName), "CanDump");
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
                SetWindowTitle("PCAN Capture", "PCAN");

                var captureWindow = new CaptureProgressWindow
                {
                    Owner = this
                };
                captureWindow.ShowDialog();

                PCAN.StopCapture();
                
                _Data = PCAN.LoadCapture();
                var (firstTimestamp, lastTimestamp) = CaptureLoadService.GetTimestampBounds(_Data);
                UpdateTimestampRange(firstTimestamp, lastTimestamp);
                EnrichUnassembledRecords(_Data);
                ApplyHighlights(_Data);

                _assembledData = AssembleFrames(_Data);
                ApplyHighlights(_assembledData);

                GenerateDeviceInfo(_assembledData);
                UpdateSrcDevices(_Data);
                UpdateSrcDevices(_assembledData);
                ActiveDataSessionService.SetCurrent(
                    "PCAN Capture",
                    "PCAN",
                    _Data,
                    _assembledData,
                    firstTimestamp,
                    lastTimestamp);
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
                UpdateRowCountText();
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
            EnsureFilterIndexes(sourceData);
            _visibleRecords = BuildVisibleRecordSet(sourceData);
            _distinctDataSeen.Clear();
            _dataGridView.Refresh();
            UpdateRowCountText();
        }

        private bool FilterRecord(object obj)
        {
            if (obj is not Nmea2000Record record)
            {
                return false;
            }

            if (_visibleRecords != null && !_visibleRecords.Contains(record))
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

        private void EnsureFilterIndexes(List<Nmea2000Record> sourceData)
        {
            if (ReferenceEquals(_indexedDataSource, sourceData) && _filterIndexes != null)
            {
                return;
            }

            _filterIndexes = BuildFilterIndexes(sourceData);
            _indexedDataSource = sourceData;
        }

        private static FilterIndexes BuildFilterIndexes(List<Nmea2000Record> records)
        {
            var indexes = new FilterIndexes();

            foreach (var record in records)
            {
                AddIndexedRecord(indexes.ByPgn, record.PGN, record);
                AddIndexedRecord(indexes.BySource, record.Source, record);
                AddIndexedRecord(indexes.ByDestination, record.Destination, record);
            }

            return indexes;
        }

        private static void AddIndexedRecord(
            Dictionary<string, List<Nmea2000Record>> index,
            string key,
            Nmea2000Record record)
        {
            if (!index.TryGetValue(key, out var records))
            {
                records = new List<Nmea2000Record>();
                index[key] = records;
            }

            records.Add(record);
        }

        private HashSet<Nmea2000Record>? BuildVisibleRecordSet(List<Nmea2000Record> sourceData)
        {
            if (_filterIndexes == null)
            {
                return null;
            }

            HashSet<Nmea2000Record>? visibleRecords = null;

            if (_includePGNs.Count > 0)
            {
                visibleRecords = UnionMatches(_filterIndexes.ByPgn, _includePGNs);
            }

            if (_includeAddresses.Count > 0)
            {
                var addressMatches = UnionMatches(_filterIndexes.BySource, _includeAddresses);
                UnionInto(addressMatches, _filterIndexes.ByDestination, _includeAddresses);
                visibleRecords = visibleRecords == null
                    ? addressMatches
                    : IntersectMatches(visibleRecords, addressMatches);
            }

            if (_excludePGNs.Count > 0)
            {
                if (visibleRecords == null)
                {
                    visibleRecords = new HashSet<Nmea2000Record>(sourceData);
                }

                RemoveMatches(visibleRecords, _filterIndexes.ByPgn, _excludePGNs);
            }

            return visibleRecords;
        }

        private static HashSet<Nmea2000Record> UnionMatches(
            Dictionary<string, List<Nmea2000Record>> index,
            HashSet<string> keys)
        {
            var matches = new HashSet<Nmea2000Record>();
            UnionInto(matches, index, keys);
            return matches;
        }

        private static void UnionInto(
            HashSet<Nmea2000Record> target,
            Dictionary<string, List<Nmea2000Record>> index,
            HashSet<string> keys)
        {
            foreach (var key in keys)
            {
                if (!index.TryGetValue(key, out var records))
                {
                    continue;
                }

                foreach (var record in records)
                {
                    target.Add(record);
                }
            }
        }

        private static HashSet<Nmea2000Record> IntersectMatches(
            HashSet<Nmea2000Record> left,
            HashSet<Nmea2000Record> right)
        {
            left.IntersectWith(right);
            return left;
        }

        private static void RemoveMatches(
            HashSet<Nmea2000Record> target,
            Dictionary<string, List<Nmea2000Record>> index,
            HashSet<string> keys)
        {
            foreach (var key in keys)
            {
                if (!index.TryGetValue(key, out var records))
                {
                    continue;
                }

                foreach (var record in records)
                {
                    target.Remove(record);
                }
            }
        }
        
        internal static List<Nmea2000Record> AssembleFrames(List<Nmea2000Record> records)
        {
            var assembledRecords = new List<Nmea2000Record>(records.Count);
            var activeMessages = new Dictionary<FastPacketKey, FastPacketMessage>(Math.Max(256, records.Count / 8)); // Track in-progress multi-frame messages

            int RowNumber = 0;
            foreach (var record in records)
            {
                int pgn = Convert.ToInt32(record.PGN);

                if (Globals.UniquePGNs.TryGetValue(pgn, out Canboat.Pgn pgnDefinition))
                {
                    if (pgnDefinition.Type == "Fast")
                    {
                        var messageKey = new FastPacketKey(record.Source, record.Destination, record.PGN);
                        var assembled = ProcessFastPacketFrame(messageKey, record, activeMessages, assembledRecords);

                        if (assembled != null)
                        {
                            assembled.LogSequenceNumber = RowNumber;
                            byte[] byteArray = assembled.PayloadBytes;

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
                        byte[] byteArray = record.PayloadBytes;

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
            FastPacketKey messageKey,
            Nmea2000Record frame,
            Dictionary<FastPacketKey, FastPacketMessage> activeMessages,
            List<Nmea2000Record> assembledRecords)
        {
            var frameData = frame.PayloadBytes;
            if (frameData.Length == 0)
            {
                return null;
            }

            int sequenceNumber = frameData[0] & 0x1F;

            // Check if this is the start of a new multi-frame message
            if (!activeMessages.ContainsKey(messageKey))
            {
                if (sequenceNumber == 0)
                {
                    // First frame: Extract TotalBytes from the second byte (ISO-TP header)
                    if (frameData.Length < 2)
                    {
                        return null;
                    }

                    int totalBytes = frameData[1];

                    if (totalBytes == 0)
                    {
                        Debug.WriteLine($"Invalid TotalBytes for {messageKey}. Skipping.");
                        return null; // Skip invalid messages
                    }

                    activeMessages[messageKey] = new FastPacketMessage
                    {
                        TotalBytes = totalBytes,
                        Frames = new Dictionary<int, byte[]>(),
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
                byte[] framePayload;

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
                    PayloadBytes = fullMessage,
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
                          Description = firstRecord.Description ?? "Unknown",
                          SourceAddresses = string.Join(", ",
                              group.Select(record => record.Source)
                                   .Where(source => !string.IsNullOrWhiteSpace(source))
                                   .Distinct(StringComparer.Ordinal)
                                   .OrderBy(source => int.TryParse(source, out var address) ? address : int.MaxValue)
                                   .ThenBy(source => source, StringComparer.Ordinal))
                       };
                   })
                .OrderByDescending(entry => entry.Count)
                .ToList();

            // Open the statistics window
            var statsWindow = new PgnStatistics(pgnCounts, ShowPgnGraph)
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
            var observedAddresses = (_Data ?? Enumerable.Empty<Nmea2000Record>())
                .Concat(_assembledData ?? Enumerable.Empty<Nmea2000Record>())
                .Select(record => record.Source)
                .Where(source => int.TryParse(source, out _))
                .Select(source => int.Parse(source, CultureInfo.InvariantCulture))
                .Distinct()
                .OrderBy(address => address)
                .ToList();

            if ((Globals.Devices == null || !Globals.Devices.Any()) && observedAddresses.Count == 0)
            {
                MessageBox.Show("No device data available.", "Device Statistics", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var unassembledCounts = (_Data ?? Enumerable.Empty<Nmea2000Record>())
                .GroupBy(record => record.Source)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            var assembledCounts = (_assembledData ?? Enumerable.Empty<Nmea2000Record>())
                .GroupBy(record => record.Source)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            var throughputBySource = (_assembledData ?? Enumerable.Empty<Nmea2000Record>())
                .GroupBy(record => record.Source)
                .ToDictionary(group => group.Key, CalculateThroughput, StringComparer.Ordinal);

            var addresses = observedAddresses
                .Concat(Globals.Devices?.Keys.Select(key => (int)key) ?? Enumerable.Empty<int>())
                .Distinct()
                .OrderBy(address => address);

            var statistics = addresses
                .Select(address =>
                {
                    var sourceKey = address.ToString(CultureInfo.InvariantCulture);
                    throughputBySource.TryGetValue(sourceKey, out var throughput);
                    Globals.Devices.TryGetValue((byte)address, out var deviceInfo);
                    return new DeviceStatisticsEntry
                    {
                        Address = deviceInfo?.Address ?? address,
                        ProductCode = deviceInfo?.ProductCode,
                        ModelID = deviceInfo?.ModelID,
                        SoftwareVersionCode = deviceInfo?.SoftwareVersionCode,
                        ModelVersion = deviceInfo?.ModelVersion,
                        ModelSerialCode = deviceInfo?.ModelSerialCode,
                        MfgCode = deviceInfo?.MfgCode,
                        DeviceClass = deviceInfo?.DeviceClass,
                        DeviceFunction = deviceInfo?.DeviceFunction,
                        UnassembledCount = unassembledCounts.GetValueOrDefault(sourceKey),
                        AssembledCount = assembledCounts.GetValueOrDefault(sourceKey),
                        AvgBpsValue = throughput.AverageBytesPerSecond ?? 0,
                        PeakBpsValue = throughput.PeakBytesPerSecond ?? 0,
                        AvgBps = FormatBps(throughput.AverageBytesPerSecond),
                        PeakBps = FormatBps(throughput.PeakBytesPerSecond)
                    };
                })
                .ToList();

              // Open the statistics window
                var devicesWindow = new Devices(statistics, ShowDeviceGraph, ShowSupportedPgns);
                devicesWindow.Show();
            }

        private void AlarmsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_assembledData == null || !_assembledData.Any())
            {
                MessageBox.Show("No assembled alarm data available.", "Alarms", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var entries = BuildAlarmHistory(_assembledData);
            if (entries.Count == 0)
            {
                MessageBox.Show("No alarm or alert history was found in the loaded capture.", "Alarms", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var window = new AlarmsWindow(entries)
            {
                Owner = this
            };
            window.Show();
        }

        private void ShowDeviceGraph(DeviceStatisticsEntry entry)
        {
            var records = GetTimestampedAssembledRecords(record =>
                string.Equals(record.Source, entry.Address.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));

            if (records.Count == 0)
            {
                MessageBox.Show("No timestamped assembled packets are available for this device.", "Traffic Graph", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var deviceName = string.IsNullOrWhiteSpace(entry.ModelID) ? entry.MfgCode ?? string.Empty : entry.ModelID;
            ShowTrafficGraph(
                $"Device {entry.Address}",
                $"Device {entry.Address}",
                deviceName,
                records);
        }

        private void ShowSupportedPgns(DeviceStatisticsEntry entry)
        {
            if (!TryGetSupportedPgnLists(entry.Address, out var transmitPgns, out var receivePgns))
            {
                MessageBox.Show("No supported PGN list is available for this device.", "Supported PGNs", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var deviceName = string.IsNullOrWhiteSpace(entry.ModelID) ? entry.MfgCode ?? string.Empty : entry.ModelID;
            var deviceLabel = string.IsNullOrWhiteSpace(deviceName)
                ? $"Device {entry.Address}"
                : $"Device {entry.Address} - {deviceName}";

            var window = new SupportedPgnsWindow(
                deviceLabel,
                transmitPgns.Select(FormatSupportedPgnEntry),
                receivePgns.Select(FormatSupportedPgnEntry))
            {
                Owner = this
            };
            window.Show();
        }

        private List<AlarmHistoryEntry> BuildAlarmHistory(IEnumerable<Nmea2000Record> records)
        {
            var entries = new List<AlarmHistoryEntry>();
            var alertTextByKey = new Dictionary<string, string>(StringComparer.Ordinal);
            var simnetMessageByKey = new Dictionary<string, string>(StringComparer.Ordinal);
            var alarmRecords = records.Where(record => AlarmHistoryPgns.Contains(record.PGN)).ToList();

            foreach (var record in alarmRecords)
            {
                var decoded = TryDecodeRecord(record);
                if (decoded == null)
                {
                    continue;
                }

                var fields = ExtractDecodedFields(decoded);
                if (record.PGN == "126985")
                {
                    var alertKey = BuildAlertKey(fields);
                    var alertText = FirstNonEmpty(
                        GetField(fields, "Alert Text Description"),
                        GetField(fields, "Alert Location Text Description"));

                    if (!string.IsNullOrWhiteSpace(alertKey) && !string.IsNullOrWhiteSpace(alertText))
                    {
                        alertTextByKey[alertKey] = alertText;
                    }
                }
                else if (record.PGN == "130856")
                {
                    var simnetKey = BuildSimnetAlarmKey(fields);
                    var messageText = GetField(fields, "Text");

                    if (!string.IsNullOrWhiteSpace(simnetKey) && !string.IsNullOrWhiteSpace(messageText))
                    {
                        simnetMessageByKey[simnetKey] = messageText;
                    }
                }
            }

            foreach (var record in alarmRecords)
            {
                var decoded = TryDecodeRecord(record);
                if (decoded == null)
                {
                    continue;
                }

                var fields = ExtractDecodedFields(decoded);
                entries.Add(new AlarmHistoryEntry
                {
                    Timestamp = record.Timestamp ?? string.Empty,
                    Source = record.Source ?? string.Empty,
                    Device = record.DeviceInfo ?? string.Empty,
                    Pgn = record.PGN ?? string.Empty,
                    Event = ClassifyAlarmEvent(record, fields),
                    Alarm = BuildAlarmLabel(record, fields, alertTextByKey, simnetMessageByKey),
                    Details = BuildAlarmDetails(record, fields)
                });
            }

            return entries
                .OrderBy(entry => TryParseAlarmTimestamp(entry.Timestamp))
                .ThenBy(entry => entry.Source, StringComparer.Ordinal)
                .ToList();
        }

        private bool TryGetSupportedPgnLists(int address, out IReadOnlyList<int> transmitPgns, out IReadOnlyList<int> receivePgns)
        {
            var transmit = new SortedSet<int>();
            var receive = new SortedSet<int>();

            if (_assembledData == null)
            {
                transmitPgns = Array.Empty<int>();
                receivePgns = Array.Empty<int>();
                return false;
            }

            var sourceText = address.ToString(CultureInfo.InvariantCulture);
            foreach (var record in _assembledData)
            {
                if (!string.Equals(record.Source, sourceText, StringComparison.Ordinal) ||
                    !string.Equals(record.PGN, "126464", StringComparison.Ordinal))
                {
                    continue;
                }

                PopulateSupportedPgnSet(record.PayloadBytes, transmit, receive);
            }

            transmitPgns = transmit.ToList();
            receivePgns = receive.ToList();
            return transmitPgns.Count > 0 || receivePgns.Count > 0;
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

        private static string FormatSupportedPgnEntry(int pgn)
        {
            var canboatRoot = ((App)Application.Current).CanboatRoot;
            if (canboatRoot == null)
            {
                return pgn.ToString(CultureInfo.InvariantCulture);
            }

            var descriptions = canboatRoot.PGNs
                .Where(definition => definition.PGN == pgn && !string.IsNullOrWhiteSpace(definition.Description))
                .Select(definition => definition.Description.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return descriptions.Count == 1
                ? $"{pgn} - {descriptions[0]}"
                : pgn.ToString(CultureInfo.InvariantCulture);
        }

        private JsonObject? TryDecodeRecord(Nmea2000Record record)
        {
            try
            {
                if (record.PGNListIndex.HasValue)
                {
                    return DecodePgnData(record.PayloadBytes, ((App)Application.Current).CanboatRoot.PGNs[record.PGNListIndex.Value]);
                }

                var pgnDefinition = ((App)Application.Current).CanboatRoot.PGNs.FirstOrDefault(q => q.PGN.ToString(CultureInfo.InvariantCulture) == record.PGN);
                return pgnDefinition == null ? null : DecodePgnData(record.PayloadBytes, pgnDefinition);
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, string> ExtractDecodedFields(JsonObject decoded)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ExtractFieldArray(result, decoded["Fields"] as JsonArray);
            ExtractFieldArray(result, decoded["RepeatedFields"] as JsonArray);
            return result;
        }

        private static void ExtractFieldArray(Dictionary<string, string> target, JsonArray? fields)
        {
            if (fields == null)
            {
                return;
            }

            foreach (var item in fields)
            {
                if (item is not JsonObject fieldObject)
                {
                    continue;
                }

                foreach (var property in fieldObject)
                {
                    var stringValue = property.Value switch
                    {
                        null => string.Empty,
                        JsonValue jsonValue when jsonValue.TryGetValue<string>(out var textValue) => textValue ?? string.Empty,
                        _ => property.Value?.ToJsonString() ?? string.Empty
                    };

                    if (!string.IsNullOrWhiteSpace(stringValue) || !target.ContainsKey(property.Key))
                    {
                        target[property.Key] = stringValue;
                    }
                }
            }
        }

        private static string GetField(IReadOnlyDictionary<string, string> fields, string fieldName)
        {
            return fields.TryGetValue(fieldName, out var value) ? value : string.Empty;
        }

        private static string BuildAlertKey(IReadOnlyDictionary<string, string> fields)
        {
            return string.Join("|", new[]
            {
                GetField(fields, "Alert Type"),
                GetField(fields, "Alert Category"),
                GetField(fields, "Alert System"),
                GetField(fields, "Alert Sub-System"),
                GetField(fields, "Alert ID"),
                GetField(fields, "Alert Occurrence Number")
            });
        }

        private static string BuildSimnetAlarmKey(IReadOnlyDictionary<string, string> fields)
        {
            return string.Join("|", new[]
            {
                GetField(fields, "Address"),
                GetField(fields, "Alarm"),
                GetField(fields, "Message ID")
            });
        }

        private static string BuildAlarmLabel(
            Nmea2000Record record,
            IReadOnlyDictionary<string, string> fields,
            IReadOnlyDictionary<string, string> alertTextByKey,
            IReadOnlyDictionary<string, string> simnetMessageByKey)
        {
            return record.PGN switch
            {
                "65288" => JoinNonEmpty(" - ",
                    GetField(fields, "Alarm Group"),
                    GetField(fields, "Alarm ID")),
                "65361" => JoinNonEmpty(" - ",
                    "Silence",
                    GetField(fields, "Alarm Group"),
                    GetField(fields, "Alarm ID")),
                "126983" or "126984" or "126985" or "126986" or "126987" or "126988" =>
                    BuildAlertLabel(fields, alertTextByKey),
                "130850" => JoinNonEmpty(" - ",
                    GetField(fields, "Alarm"),
                    FirstNonEmpty(
                        LookupOrEmpty(simnetMessageByKey, BuildSimnetAlarmKey(fields)),
                        GetField(fields, "Message ID"))),
                "130856" => FirstNonEmpty(
                    GetField(fields, "Text"),
                    JoinNonEmpty(" - ", "Simnet Alarm Message", GetField(fields, "Message ID"))),
                _ => record.Description ?? string.Empty
            };
        }

        private static string BuildAlertLabel(
            IReadOnlyDictionary<string, string> fields,
            IReadOnlyDictionary<string, string> alertTextByKey)
        {
            var alertKey = BuildAlertKey(fields);
            var text = LookupOrEmpty(alertTextByKey, alertKey);

            return FirstNonEmpty(
                text,
                JoinNonEmpty(" - ",
                    GetField(fields, "Alert Type"),
                    GetField(fields, "Alert Category"),
                    JoinNonEmpty("/",
                        GetField(fields, "Alert System"),
                        GetField(fields, "Alert Sub-System"),
                        GetField(fields, "Alert ID"))));
        }

        private static string BuildAlarmDetails(Nmea2000Record record, IReadOnlyDictionary<string, string> fields)
        {
            return record.PGN switch
            {
                "65288" => JoinNonEmpty("; ",
                    GetField(fields, "Alarm Status"),
                    ValueWithLabel("Priority", GetField(fields, "Alarm Priority"))),
                "65361" => "Silence request",
                "126983" => JoinNonEmpty("; ",
                    ValueWithLabel("State", GetField(fields, "Alert State")),
                    ValueWithLabel("Ack", GetField(fields, "Acknowledge Status")),
                    ValueWithLabel("Silence", GetField(fields, "Temporary Silence Status")),
                    ValueWithLabel("Escalation", GetField(fields, "Escalation Status")),
                    ValueWithLabel("Trigger", GetField(fields, "Trigger Condition")),
                    ValueWithLabel("Threshold", GetField(fields, "Threshold Status"))),
                "126984" => JoinNonEmpty("; ",
                    ValueWithLabel("Response", GetField(fields, "Response Command")),
                    ValueWithLabel("Occurrence", GetField(fields, "Alert Occurrence Number"))),
                "126985" => JoinNonEmpty("; ",
                    GetField(fields, "Alert Text Description"),
                    GetField(fields, "Alert Location Text Description")),
                "126986" => JoinNonEmpty("; ",
                    ValueWithLabel("Control", GetField(fields, "Alert Control")),
                    ValueWithLabel("User Assignment", GetField(fields, "User Defined Alert Assignment")),
                    ValueWithLabel("Reactivation", GetField(fields, "Reactivation Period")),
                    ValueWithLabel("Silence Period", GetField(fields, "Temporary Silence Period")),
                    ValueWithLabel("Escalation Period", GetField(fields, "Escalation Period"))),
                "126987" => JoinNonEmpty("; ",
                    ValueWithLabel("Parameter", GetField(fields, "Parameter Number")),
                    ValueWithLabel("Method", GetField(fields, "Trigger Method")),
                    ValueWithLabel("Threshold", GetField(fields, "Threshold Level"))),
                "126988" => JoinNonEmpty("; ",
                    ValueWithLabel("Parameter", GetField(fields, "Value Parameter Number")),
                    ValueWithLabel("Value", GetField(fields, "Value Data"))),
                "130850" => JoinNonEmpty("; ",
                    ValueWithLabel("Alarm", GetField(fields, "Alarm")),
                    ValueWithLabel("Message ID", GetField(fields, "Message ID"))),
                "130856" => GetField(fields, "Text"),
                _ => string.Empty
            };
        }

        private static string ClassifyAlarmEvent(Nmea2000Record record, IReadOnlyDictionary<string, string> fields)
        {
            var lowerAlertState = GetField(fields, "Alert State").ToLowerInvariant();
            var lowerSilence = GetField(fields, "Temporary Silence Status").ToLowerInvariant();
            var lowerAck = GetField(fields, "Acknowledge Status").ToLowerInvariant();
            var lowerResponse = GetField(fields, "Response Command").ToLowerInvariant();
            var lowerSeatalkStatus = GetField(fields, "Alarm Status").ToLowerInvariant();

            return record.PGN switch
            {
                "65288" when lowerSeatalkStatus.Contains("not met") || lowerSeatalkStatus.Contains("inactive") => "Cleared",
                "65288" => "Triggered",
                "65361" => "Silenced",
                "126983" when lowerSilence.Contains("silence") && !lowerSilence.Contains("not") => "Silenced",
                "126983" when lowerAck.Contains("acknowledged") => "Acknowledged",
                "126983" when lowerAlertState.Contains("normal") || lowerAlertState.Contains("inactive") || lowerAlertState.Contains("cleared") => "Cleared",
                "126983" => "Triggered",
                "126984" when lowerResponse.Contains("suppress") => "Suppressed",
                "126984" when lowerResponse.Contains("silence") => "Silenced",
                "126984" when lowerResponse.Contains("ack") => "Acknowledged",
                "126984" when lowerResponse.Contains("clear") || lowerResponse.Contains("reset") => "Cleared",
                "126984" => "Response",
                "126985" => "Text",
                "126986" => "Configured",
                "126987" => "Threshold",
                "126988" => "Value",
                "130850" => "Triggered",
                "130856" => "Message",
                _ => record.Description ?? "Alarm"
            };
        }

        private static string LookupOrEmpty(IReadOnlyDictionary<string, string> map, string key)
        {
            return string.IsNullOrWhiteSpace(key) ? string.Empty : map.TryGetValue(key, out var value) ? value : string.Empty;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        private static string JoinNonEmpty(string separator, params string[] values)
        {
            return string.Join(separator, values.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        private static string ValueWithLabel(string label, string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : $"{label}: {value}";
        }

        private static DateTimeOffset? TryParseAlarmTimestamp(string timestamp)
        {
            if (string.IsNullOrWhiteSpace(timestamp))
            {
                return null;
            }

            if (DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var absolute))
            {
                return absolute;
            }

            if (double.TryParse(timestamp, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return DateTimeOffset.UnixEpoch.AddSeconds(seconds);
            }

            if (TimeSpan.TryParse(timestamp, CultureInfo.InvariantCulture, out var relative))
            {
                return DateTimeOffset.UnixEpoch.Add(relative);
            }

            return null;
        }

        private void ShowPgnGraph(PgnStatisticsEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.PGN))
            {
                return;
            }

            var records = GetTimestampedAssembledRecords(record =>
                string.Equals(record.PGN, entry.PGN, StringComparison.Ordinal));

            if (records.Count == 0)
            {
                MessageBox.Show("No timestamped assembled packets are available for this PGN.", "Traffic Graph", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ShowTrafficGraph(
                $"PGN {entry.PGN}",
                $"PGN {entry.PGN}",
                entry.Description ?? string.Empty,
                records);
        }

        private List<DateTimeOffset> GetTimestampedAssembledRecords(Func<Nmea2000Record, bool> predicate)
        {
            return (_assembledData ?? Enumerable.Empty<Nmea2000Record>())
                .Where(predicate)
                .Select(record => new
                {
                    Timestamp = TryGetRecordTimestamp(record)
                })
                .Where(item => item.Timestamp.HasValue)
                .Select(item => item.Timestamp!.Value)
                .OrderBy(timestamp => timestamp)
                .ToList();
        }

        private void ShowTrafficGraph(
            string graphTitle,
            string graphSeriesName,
            string graphSubtitle,
            IReadOnlyList<DateTimeOffset> records)
        {
            var graphStart = records.First().UtcDateTime;
            var graphEnd = records.Last().UtcDateTime;
            if (graphEnd <= graphStart)
            {
                graphEnd = graphStart.AddSeconds(1);
            }

            var graphWindow = new TrafficGraphWindow(
                graphTitle,
                graphSeriesName,
                graphSubtitle,
                records,
                new DateTimeOffset(graphStart, TimeSpan.Zero),
                new DateTimeOffset(graphEnd, TimeSpan.Zero))
            {
                Owner = this
            };
            graphWindow.Show();
        }

        private static (double? AverageBytesPerSecond, double? PeakBytesPerSecond) CalculateThroughput(
            IEnumerable<Nmea2000Record> records)
        {
            var timestampedRecords = records
                .Select(record => new
                {
                    Timestamp = TryGetRecordTimestamp(record),
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

        private static DateTimeOffset? TryGetRecordTimestamp(Nmea2000Record record)
        {
            if (string.IsNullOrWhiteSpace(record.Timestamp))
            {
                return null;
            }

            if (double.TryParse(record.Timestamp, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                var wholeSeconds = Math.Truncate(seconds);
                var fractionalSeconds = seconds - wholeSeconds;
                return DateTimeOffset.UnixEpoch
                    .AddSeconds(wholeSeconds)
                    .AddTicks((long)Math.Round(fractionalSeconds * TimeSpan.TicksPerSecond));
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

        private static string FormatBps(double? bytesPerSecond)
        {
            return bytesPerSecond.HasValue
                ? bytesPerSecond.Value.ToString("0.##", CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private void ClearData()
        {
            // Clear data collections
            _Data?.Clear();
            _assembledData?.Clear();
            _filterDebounceTimer.Stop();
            _dataGridView = null;
            _distinctDataSeen.Clear();
            _visibleRecords = null;
            _indexedDataSource = null;
            _filterIndexes = null;
            _includePGNs.Clear();
            _includeAddresses.Clear();
            _excludePGNs.Clear();
            _distinctFilterEnabled = false;
            Globals.Devices.Clear();
            ActiveDataSessionService.Clear();

            // Reset the DataGrid and data view
            DataGrid.ItemsSource = null;
            JsonViewerTextBox.Text = null;

            IncludePGNTextBox.Text = string.Empty;
            ExcludePGNTextBox.Text = string.Empty;
            IncludeAddressTextBox.Text = string.Empty;
            UpdateRowCountText();
        }

        private void LoadHighlights()
        {
            _highlightBackgroundsByPgn.Clear();

            var highlightPath = Path.Combine(AppContext.BaseDirectory, "highlight.json");
            if (!File.Exists(highlightPath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(highlightPath);
                var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (entries == null)
                {
                    return;
                }

                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Key) ||
                        string.IsNullOrWhiteSpace(entry.Value) ||
                        !TryParseBrush(entry.Value, out var brush))
                    {
                        continue;
                    }

                    _highlightBackgroundsByPgn[entry.Key.Trim()] = brush;
                }
            }
            catch
            {
                // Ignore malformed highlight.json and continue with default styling.
            }
        }

        private static bool TryParseBrush(string colorValue, out Brush brush)
        {
            brush = Brushes.Black;

            try
            {
                var converted = ColorConverter.ConvertFromString(colorValue);
                if (converted is Color color)
                {
                    var solidBrush = new SolidColorBrush(color);
                    solidBrush.Freeze();
                    brush = solidBrush;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private void ApplyHighlights(IEnumerable<Nmea2000Record>? records)
        {
            if (records == null)
            {
                return;
            }

            foreach (var record in records)
            {
                if (record.PGN != null && _highlightBackgroundsByPgn.TryGetValue(record.PGN, out var brush))
                {
                    record.HighlightBackground = brush;
                }
                else
                {
                    record.HighlightBackground = Brushes.Transparent;
                }
            }
        }

        private List<Nmea2000Record>? GetActiveBaseData()
        {
            return _currentPacketView == PacketViewMode.Assembled ? _assembledData : _Data;
        }

        private static byte[] ParseRecordData(Nmea2000Record record)
        {
            return record.PayloadBytes;
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

        internal static void EnrichUnassembledRecords(List<Nmea2000Record>? records)
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

        private List<Nmea2000Record>? GetCurrentSourceData()
        {
            return _currentPacketView == PacketViewMode.Assembled ? _assembledData : _Data;
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
        private void UpdateTimestampRange(DateTimeOffset? firstTimestamp, DateTimeOffset? lastTimestamp)
        {
            if (!firstTimestamp.HasValue || !lastTimestamp.HasValue)
            {
                TimestampRangeText.Text = _Data == null || !_Data.Any()
                    ? "No data loaded"
                    : "No valid timestamps found";
                return;
            }

            TimestampRangeText.Text = $"Timestamp Range: {firstTimestamp.Value:G} - {lastTimestamp.Value:G}";
        }

        private void UpdateRowCountText()
        {
            if (_dataGridView == null)
            {
                RowCountText.Text = "Rows: 0";
                return;
            }

            RowCountText.Text = $"Rows: {_dataGridView.Cast<object>().Count():N0}";
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
                    var dataBytes = selectedRecord.PayloadBytes;

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
            var visibleRecords = DataGrid.Items
                .OfType<Nmea2000Record>()
                .ToList();

            if (visibleRecords.Count == 0 ||
                !Regex.IsMatch(PgnSearchTextBox.Text ?? string.Empty, @"^\d{5,6}$"))
            {
                return;
            }

            string searchText = PgnSearchTextBox.Text.Trim();
            var currentSelectedRecord = DataGrid.SelectedItem as Nmea2000Record;
            var currentVisibleIndex = currentSelectedRecord == null
                ? _currentSearchIndex
                : visibleRecords.IndexOf(currentSelectedRecord);

            int startIndex = forward ? currentVisibleIndex + 1 : currentVisibleIndex - 1;

            // Wrap-around behavior
            if (startIndex >= visibleRecords.Count)
                startIndex = 0;
            else if (startIndex < 0)
                startIndex = visibleRecords.Count - 1;

            for (int i = 0; i < visibleRecords.Count; i++)
            {
                int index = (startIndex + (forward ? i : -i + visibleRecords.Count)) % visibleRecords.Count;
                var matchedRecord = visibleRecords[index];
                if (matchedRecord.PGN != null && matchedRecord.PGN.Contains(searchText))
                {
                    _currentSearchIndex = index;

                    DataGrid.UnselectAll();
                    DataGrid.SelectedItem = matchedRecord;

                    if (DataGrid.Columns.Count > 0)
                    {
                        DataGrid.CurrentCell = new DataGridCellInfo(matchedRecord, DataGrid.Columns[0]);
                    }

                    DataGrid.ScrollIntoView(matchedRecord);
                    DataGrid.Focus();

                    return;
                }
            }

            // If no match found
            MessageBox.Show("No matching PGN found.", "Search Result", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        // Enriches DataGrid with device information if available
        internal static void UpdateSrcDevices(List<Nmea2000Record> data)
        {
            foreach (var record in data)
            {
                Globals.Devices.TryGetValue(Convert.ToByte(record.Source), out var result);
                if (result != null)
                {
                    // Raymarine product codes are not really informative, use ModelVersion instead
                    if (!string.IsNullOrWhiteSpace(result.ModelID) && Regex.IsMatch(result.ModelID, @"^[A-Z]\d{5}$"))
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
                var recordsToExport = GetRecordsForCsvExport(selectedItem);
                var csvRows = recordsToExport.Select(BuildCsvRow);
                Clipboard.SetText(string.Join(Environment.NewLine, csvRows));
            }
        }

        private IEnumerable<Nmea2000Record> GetRecordsForCsvExport(Nmea2000Record clickedRecord)
        {
            var selectedRecords = DataGrid.SelectedItems
                .OfType<Nmea2000Record>()
                .ToList();

            if (selectedRecords.Count > 1 && selectedRecords.Contains(clickedRecord))
            {
                return selectedRecords
                    .OrderBy(record => record.LogSequenceNumber)
                    .ToList();
            }

            return new[] { clickedRecord };
        }

        private static string BuildCsvRow(Nmea2000Record record)
        {
            var csvValues = new[]
            {
                record.LogSequenceNumber.ToString(CultureInfo.InvariantCulture),
                record.Timestamp ?? string.Empty,
                record.Source ?? string.Empty,
                record.DeviceInfo ?? string.Empty,
                record.Destination ?? string.Empty,
                record.PGN ?? string.Empty,
                record.Type ?? string.Empty,
                record.Priority ?? string.Empty,
                record.Description ?? string.Empty,
                record.Data ?? string.Empty
            };

            return string.Join(",", csvValues.Select(EscapeCsvValue));
        }

        private static string EscapeCsvValue(string value)
        {
            if (value.Contains('"'))
            {
                value = value.Replace("\"", "\"\"");
            }

            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                ? $"\"{value}\""
                : value;
        }
    }

}
