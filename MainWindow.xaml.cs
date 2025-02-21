using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using static NMEA2000Analyzer.PgnDefinitions;
using Application = System.Windows.Application;

namespace NMEA2000Analyzer
{
    public partial class MainWindow : Window
    {

        private List<Nmea2000Record>? _Data;
        private List<Nmea2000Record>? _assembledData;
        private List<Nmea2000Record>? _filteredData;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => PopulatePresetsMenu();
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
            public int LogSequenceNumber { get; set; }
            public string? Timestamp { get; set; }
            public string Source { get; set; }
            public string Destination { get; set; }
            public string PGN { get; set; }
            public string Type { get; set; }
            public string Priority { get; set; }
            public string Description { get; set; } = ""; // Placeholder if not provided
            public string Data { get; set; }
            public string? DeviceInfo { get; set; }
            public int? PGNListIndex { get; set; }
        }

        private class FastPacketMessage
        {
            public int TotalBytes { get; set; }
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

        private async void OpenMenuItem_ClickAsync(object sender, RoutedEventArgs e)
        {
            // Open file picker dialog
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "(*.csv, *.can, *.log, *.txt, *.dump)|*.csv;*.can;*.log;*.txt;*.dump|All Files (*.*)|*.*",
                Title = "Open File"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                ClearData();
                string filePath = openFileDialog.FileName;

                var format = FileFormats.DetectFileFormat(filePath);
                Title = $"NMEA2000 Analyzer - {Path.GetFileName(filePath)} ({Enum.GetName(typeof(FileFormats.FileFormat), format)})";

                // Parse based on detected format
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
                    case FileFormats.FileFormat.YDBinary:
                        _Data = await Task.Run(() => FileFormats.LoadYDBinary(filePath));
                        break;
                    default:
                        MessageBox.Show("Unsupported or unknown file format.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                }

                DataGrid.ItemsSource = _Data; // Bind parsed data to DataGrid
                UpdateTimestampRange();

                try
                {
                    _assembledData = AssembleFrames(_Data);
                    DataGrid.ItemsSource = _assembledData;
                    GenerateDeviceInfo(_assembledData);
                    UpdateSrcDevices(_assembledData);
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
            var includePGNs = ParseList(IncludePGNTextBox.Text);
            var includeAddress = ParseList(IncludeAddressTextBox.Text);
            var excludePGNs = ParseList(ExcludePGNTextBox.Text);

            // Apply filters to the original data
            var filteredData = _assembledData.Where(record =>
            {
                bool includePGN = includePGNs.Count == 0 || includePGNs.Contains(record.PGN);
                bool includeSource = includeAddress.Count == 0
                    || includeAddress.Contains(record.Source)
                    || includeAddress.Contains(record.Destination);
                bool exclude = excludePGNs.Contains(record.PGN);

                return includePGN && includeSource && !exclude;
            }).ToList();

            {
                filteredData = filteredData
                    .GroupBy(record => record.Data)  // Group by the Data column
                    .Select(group => group.First()) // Take the first record from each group
                    .ToList();                      // Convert to a List<Nmea2000Record>
            }

            DataGrid.ItemsSource = filteredData;
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
            var statsWindow = new PgnStatistics(pgnCounts);
            statsWindow.Show();
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
            _filteredData?.Clear();
            Globals.Devices.Clear();

            // Reset the DataGrid and data view
            DataGrid.ItemsSource = null;
            JsonViewerTextBox.Text = null;

            // Optionally, clear filters if needed
            IncludePGNTextBox.Text = string.Empty;
            ExcludePGNTextBox.Text = string.Empty;
            IncludeAddressTextBox.Text = string.Empty;
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
                IncludePGNTextBox.Text = includePGNs;
                ApplyFilters();
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
                    Tag = preset.IncludePGNs // Store the PGN list in the Tag property
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
                ApplyFilters();
            }
        }

        private void IncludeAddressMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.CommandParameter is Nmea2000Record selectedRow)
            {
                // Set the Include Filter to the selected PGN
                IncludeAddressTextBox.Text = selectedRow.Source;

                // Apply the filter
                ApplyFilters();
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
            ApplyFilters();
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

                    // Convert the decoded output to a formatted JSON string
                    string jsonString = JsonSerializer.Serialize(decodedJson, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    // Open the JSON Viewer Window
                    JsonViewerTextBox.Text = jsonString;
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

    public class HexToAsciiConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            string hexString = value as string;
            return hexString != null ? ConvertHexToAscii(hexString) : string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        private string ConvertHexToAscii(string hexString)
        {
            string[] hexValues = hexString.Split(' ');
            char[] asciiChars = new char[hexValues.Length];

            for (int i = 0; i < hexValues.Length; i++)
            {
                string hex = hexValues[i].Replace("0x", "");
                int byteValue = int.Parse(hex, System.Globalization.NumberStyles.HexNumber);

                asciiChars[i] = byteValue >= 32 && byteValue <= 126 ? (char)byteValue : '.';
            }

            return new string(asciiChars);
        }
    }
}
