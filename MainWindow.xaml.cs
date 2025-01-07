using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.IO;
using static NMEA2000Analyzer.PgnDefinitions;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace NMEA2000Analyzer
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => PopulatePresetsMenu();
        }

        private List<Nmea2000Record>? _Data;
        private List<Nmea2000Record>? _assembledData;
        private List<Nmea2000Record>? _filteredData;
        private Dictionary<string, CanboatPgn> pgnDefinitions;

        // Class to hold parsed data for the DataGrid
        public class Nmea2000Record
        {
            public int LogSequenceNumber { get; set; }
            public string? Timestamp { get; set; }
            public string Source { get; set; }
            public string Destination { get; set; }
            public string PGN { get; set; }
            public string Type { get; set; }
            public string Priority { get; set; }
            public string Description { get; set; } = ""; // Placeholder if not provided
            public string Data { get; set; }
        }

        private class FastPacketMessage
        {
            public int TotalBytes { get; set; }
            public Dictionary<int, string[]> Frames { get; set; } = new Dictionary<int, string[]>();
            public string Source { get; set; }
            public string Destination { get; set; }
            public string PGN { get; set; }
            public string Priority { get; set; }
        }

        public class Preset
        {
            public string Name { get; set; }
            public string IncludePGNs { get; set; }
        }

        private async void OpenMenuItem_ClickAsync(object sender, RoutedEventArgs e)
        {
            // Open file picker dialog
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "(*.csv, *.log, *.txt)|*.csv;*.log;*.txt|All Files (*.*)|*.*",
                Title = "Open File"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                ClearData();
                string filePath = openFileDialog.FileName;
                this.Title = $"NMEA2000 Analyzer - {System.IO.Path.GetFileName(filePath)}";

                var format = FileFormats.DetectFileFormat(filePath);

                // Parse based on detected format
                switch (format)
                {
                    case FileFormats.FileFormat.TwoCanCsv:
                        _Data = await Task.Run(() => FileFormats.LoadTwoCanCsv(filePath));
                        break;
                    case FileFormats.FileFormat.Actisense:
                        _Data = await Task.Run(() => FileFormats.LoadActisense(filePath));
                        break;
                    case FileFormats.FileFormat.CanDump:
                        _Data = await Task.Run(() => FileFormats.LoadCanDump(filePath));
                        break;
                    case FileFormats.FileFormat.YDWG:
                        _Data = await Task.Run(() => FileFormats.LoadYDWGLog(filePath));
                        break;
                    default:
                        MessageBox.Show("Unsupported or unknown file format.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                }

                DataGrid.ItemsSource = _Data; // Bind parsed data to DataGrid
                UpdateTimestampRange();

                try
                {
                    pgnDefinitions = await LoadPgnDefinitionsAsync();
                    Enrich(pgnDefinitions);
                    _assembledData = AssembleFrames(_Data, pgnDefinitions);
                    DataGrid.ItemsSource = _assembledData;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        private void FilterTextBoxes_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void ApplyFilters()
        {
            if (_assembledData == null) return;

            // Parse Include and Exclude PGNs
            var includePGNs = ParsePGNList(IncludeFilterTextBox.Text);
            var excludePGNs = ParsePGNList(ExcludeFilterTextBox.Text);

            // Apply filters to the original data
            var filteredData = _assembledData.Where(record =>
            {
                bool include = includePGNs.Count == 0 || includePGNs.Contains(record.PGN);
                bool exclude = excludePGNs.Contains(record.PGN);
                return include && !exclude;
            }).ToList();

            DataGrid.ItemsSource = filteredData;
        }

        private HashSet<string> ParsePGNList(string input)
        {
            // Split comma-separated PGNs and normalize (trim whitespace)
            return input.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(pgn => pgn.Trim())
                        .ToHashSet();
        }
        private void Enrich(Dictionary<string, CanboatPgn> pgnDefinitions)
        {
            foreach (var record in _Data)
            {
                // Directly check if the PGN exists in the dictionary
                if (pgnDefinitions.TryGetValue(record.PGN, out var pgnDefinition))
                {
                    record.Description = pgnDefinition.Description; // Update the description
                    record.Type = pgnDefinition.Type;
                }
                else
                {
                    record.Description = "Unknown PGN"; // Handle missing PGNs
                    record.Type = "Unknown";
                }
            }
        }

        private List<Nmea2000Record> AssembleFrames(List<Nmea2000Record> records, Dictionary<string, CanboatPgn> pgnDefinitions)
        {
            var assembledRecords = new List<Nmea2000Record>();
            var activeMessages = new Dictionary<string, FastPacketMessage>(); // Track in-progress multi-frame messages

            foreach (var record in records)
            {
                // Check if the PGN exists in the definitions
                if (pgnDefinitions.TryGetValue(record.PGN, out var pgnDefinition))
                {
                    // Use the Type property to determine if it's Single or Fast
                    if (pgnDefinition.Type == "Fast")
                    {
                        var messageKey = $"{record.Source}-{record.Destination}-{record.PGN}";
                        ProcessFastPacketFrame(messageKey, record, activeMessages, assembledRecords, pgnDefinitions);
                    }
                    else
                    {
                        // Single-frame message
                        record.Description = pgnDefinition.Description;
                        record.Type = pgnDefinition.Type;
                        assembledRecords.Add(record);
                    }
                }
                else
                {
                    // Unknown PGN - treat as single-frame
                    record.Description = "Unknown PGN";
                    record.Type = "Unknown";
                    assembledRecords.Add(record);
                }
            }

            return assembledRecords;
        }

        private void ProcessFastPacketFrame(
            string messageKey,
            Nmea2000Record frame,
            Dictionary<string, FastPacketMessage> activeMessages,
            List<Nmea2000Record> assembledRecords,
            Dictionary<string, CanboatPgn> pgnDefinitions)
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
                        return; // Skip invalid messages
                    }

                    activeMessages[messageKey] = new FastPacketMessage
                    {
                        TotalBytes = totalBytes,
                        Frames = new Dictionary<int, string[]>(),
                        Source = frame.Source,
                        Destination = frame.Destination,
                        PGN = frame.PGN,
                        Priority = frame.Priority
                    };

                    // Debug.WriteLine($"Started new message for {messageKey}: TotalBytes = {totalBytes}");
                }
                else
                {
                    // If the first frame hasn't been received yet, skip this frame
                    Debug.WriteLine($"First frame missing for {messageKey}. Skipping frame {sequenceNumber}.");
                    return;
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
                //Debug.WriteLine($"Added frame {sequenceNumber} to {messageKey}: {string.Join(" ", framePayload)}");
            }
            else
            {
                Debug.WriteLine($"Duplicate frame {sequenceNumber} for {messageKey}. Ignoring.");
            }

            // Calculate the total number of bytes received so far
            int receivedBytes = message.Frames.Values.Sum(f => f.Length);
            //Debug.WriteLine($"Message {messageKey}: ReceivedBytes = {receivedBytes}, TotalBytes = {message.TotalBytes}");

            // Check if the message is complete
            if (message.TotalBytes > 0 && receivedBytes >= message.TotalBytes)
            {
                // Assemble the full message
                var fullMessage = message.Frames.OrderBy(f => f.Key) // Order frames by sequence number
                                                .SelectMany(f => f.Value) // Concatenate all frame data
                                                .Take(message.TotalBytes) // Take only the required number of bytes
                                                .ToArray();

                //Debug.WriteLine($"Assembled message for {messageKey}: {string.Join(" ", fullMessage)}");

                // Retrieve PGN details for Description and Type
                string description = "Unknown PGN";
                string type = "Unknown";

                if (pgnDefinitions.TryGetValue(message.PGN, out var pgnDefinition))
                {
                    description = pgnDefinition.Description;
                    type = pgnDefinition.Type;
                }

                // Add the assembled message to the final records
                assembledRecords.Add(new Nmea2000Record
                {
                    Source = message.Source,
                    Destination = message.Destination,
                    PGN = message.PGN,
                    Priority = message.Priority,
                    Data = string.Join(" ", fullMessage),
                    Description = description,
                    Type = type
                });

                // Remove the completed message from activeMessages
                activeMessages.Remove(messageKey);
            }
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
                    string description = "Unknown PGN";
                    if (pgnDefinitions.TryGetValue(group.Key, out var pgnDefinition))
                    {
                        description = pgnDefinition.Description;
                    }

                    return new PgnStatisticsEntry
                    {
                        PGN = group.Key,
                        Count = group.Count(),
                        Description = description
                    };
                })
                .OrderByDescending(entry => entry.Count)
                .ToList();

            // Open the statistics window
            var statsWindow = new PgnStatistics(pgnCounts);
            statsWindow.Show();
        }
        private void ClearData()
        {
            // Clear data collections
            _Data?.Clear();
            _assembledData?.Clear();
            _filteredData?.Clear();

            // Reset the DataGrid
            DataGrid.ItemsSource = null;

            // Optionally, clear filters if needed
            IncludeFilterTextBox.Text = string.Empty;
            ExcludeFilterTextBox.Text = string.Empty;
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
            if (sender is MenuItem menuItem && menuItem.Tag is string includePGNs)
            {
                IncludeFilterTextBox.Text = includePGNs;
                ApplyFilters();
            }
        }

        private void PopulatePresetsMenu()
        {
            var presets = LoadPresets(); // Load presets from the JSON file

            // Sort presets alphabetically by Name
            var sortedPresets = presets.OrderBy(p => p.Name).ToList();

            foreach (var preset in sortedPresets)
            {
                var menuItem = new MenuItem
                {
                    Header = preset.Name,
                    Tag = preset.IncludePGNs // Store the PGN list in the Tag property
                };
                menuItem.Click += PresetsItem_Click;
                Presets.Items.Add(menuItem); // Add the menu item to the PresetsMenu
            }
        }

        private void IncludePgnMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.CommandParameter is string pgn)
            {
                // Set the Include Filter to the selected PGN
                IncludeFilterTextBox.Text = pgn;

                // Apply the filter
                ApplyFilters();
            }
        }

        private void ReferencePgnMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.CommandParameter is string pgn)
            {
                try
                {
                    // Construct the URL
                    var url = $"https://canboat.github.io/canboat/canboat.html#pgn-{pgn}";

                    // Open the URL in the default browser
                    System.Diagnostics.Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open reference URL for PGN {pgn}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
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
    }
}