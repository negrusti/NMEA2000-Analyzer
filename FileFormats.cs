using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using static NMEA2000Analyzer.MainWindow;

namespace NMEA2000Analyzer
{
    class FileFormats
    {
        public enum FileFormat
        {
            Unknown,
            TwoCanCsv,
            Actisense,
            CanDump,
            YDWG,
            PCANView
        }

        public static FileFormat DetectFileFormat(string filePath)
        {
            try
            {
                var lines = File.ReadLines(filePath).Take(10).ToList(); // Read the first few lines

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (line.Contains(","))
                    {
                        // CSV: Look for a header or typical CSV structure
                        if (line.StartsWith("Source,Destination,PGN,Priority,"))
                        {
                            Debug.WriteLine("TwoCanCSV format detected");
                            return FileFormat.TwoCanCsv;
                        }

                        // Actisense: Look for timestamp and structured data
                        if (DateTime.TryParse(line.Split(',')[0], out _))
                        {
                            Debug.WriteLine("Actisense format detected");
                            return FileFormat.Actisense;
                        }
                    }
                    else if (line.Contains("#") && line.Contains("can"))
                    {
                        // CanDump: Look for CAN format pattern
                        if (Regex.IsMatch(line, @"^\(\d+\.\d+\)\s+can\d+\s+\w+#\w+"))
                        {
                            Debug.WriteLine("CanDump format detected");
                            return FileFormat.CanDump;
                        }
                    }
                    else if (Regex.IsMatch(line, @"^\d{2}:\d{2}:\d{2}\.\d{3}\s+[RT]\s+\w{8}\s+"))
                    {
                        // YDWG: Look for timestamp and raw CAN ID
                        Debug.WriteLine("YDWG format detected");
                        return FileFormat.YDWG;
                    }
                    else if (line.StartsWith(";$FILEVERSION=") || line.StartsWith(";$STARTTIME="))
                    {
                        // PCAN-View: Look for PCAN-View header or message format
                        Debug.WriteLine("PCAN-View format detected");
                        return FileFormat.PCANView;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to detect file format: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return FileFormat.Unknown; // Default to unknown format
        }

        public static List<Nmea2000Record> LoadTwoCanCsv(string filePath)
        {
            var records = new List<Nmea2000Record>();

            using (var reader = new StreamReader(filePath))
            {
                string? headerLine = reader.ReadLine(); // Read the header
                if (headerLine == null) throw new Exception("File is empty.");

                // Validate header format
                var headers = headerLine.Split(',').Select(h => h.Trim()).ToArray();
                if (!headers.Contains("Source") || !headers.Contains("Destination") || !headers.Contains("PGN") || !headers.Contains("Priority"))
                {
                    throw new Exception("Invalid CSV format. Required columns: Source, Destination, PGN, Priority, D1-D8.");
                }

                // Read and parse the rest of the file
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var values = line.Split(',').Select(v => v.Trim()).ToArray();
                    if (values.Length < 12) continue; // Ensure there are enough columns (D1-D8)

                    var record = new Nmea2000Record
                    {
                        Source = values[0],
                        Destination = values[1],
                        PGN = values[2],
                        Priority = values[3],
                        Data = string.Join(" ", values.Skip(4).Take(8)) // Combine D1-D8 as hex values with spaces
                    };
                    records.Add(record);
                }
            }

            return records;
        }

        public static List<Nmea2000Record> LoadCanDump(string filePath)
        {
            var records = new List<Nmea2000Record>();

            // Regular expression to match the log format
            var regex = new Regex(@"\((?<timestamp>[\d.]+)\)\s+(?<interface>\S+)\s+(?<canId>[0-9A-F]+)#(?<data>[0-9A-F]*)");

            foreach (var line in System.IO.File.ReadLines(filePath))
            {
                var match = regex.Match(line);
                if (!match.Success) continue;

                try
                {
                    // Parse the timestamp
                    var timestamp = double.Parse(match.Groups["timestamp"].Value, CultureInfo.InvariantCulture);
                    var datetime = DateTimeOffset.FromUnixTimeSeconds((long)timestamp)
                        .AddMilliseconds((timestamp % 1) * 1000);

                    // Parse the CAN ID
                    var canIdHex = match.Groups["canId"].Value;
                    int canId = int.Parse(canIdHex, NumberStyles.HexNumber);

                    // Extract PGN, Source, and Priority from CAN ID
                    int source = canId & 0xFF;
                    int pgn = (canId >> 8) & 0x1FFFF;
                    int priority = (canId >> 26) & 0x7;

                    // Parse the CAN data
                    var data = match.Groups["data"].Value;
                    var dataFormatted = string.Join(" ", Regex.Matches(data, "..").Select(m => $"0x{m.Value}"));

                    // Create an Nmea2000Record
                    records.Add(new Nmea2000Record
                    {
                        Timestamp = datetime.ToString("o"), // ISO 8601 format
                        Source = source.ToString(),
                        Destination = "255", // Default for broadcast
                        PGN = pgn.ToString(),
                        Priority = priority.ToString(),
                        Data = dataFormatted,
                        Description = "Unknown PGN",
                        Type = "Unknown"
                    });
                }
                catch (Exception ex)
                {
                    // Log or handle parse errors for individual lines
                    Console.WriteLine($"Failed to parse line: {line}. Error: {ex.Message}");
                }
            }

            return records;
        }
        public static List<Nmea2000Record> LoadActisense(string filePath)
        {
            var records = new List<Nmea2000Record>();

            using (var reader = new StreamReader(filePath))
            {
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var values = line.Split(',').Select(v => v.Trim()).ToArray();
                    if (values.Length < 8) continue; // Ensure there are enough columns (PGN, Source, Destination, and Data)

                    // Parse the record
                    var record = new Nmea2000Record
                    {
                        Timestamp = values[0], // Use raw timestamp for now, parsing can be added if needed
                        Priority = values[1],
                        PGN = values[2],
                        Source = values[3],
                        Destination = values[4],
                        Data = string.Join(" ", values.Skip(6).Select(d => $"0x{d.ToUpper()}").ToArray()) // Combine data bytes as a space-separated string
                    };
                    //Debug.WriteLine(record.Data);
                    records.Add(record);
                }
            }

            return records;
        }

        public static List<Nmea2000Record> LoadYDWGLog(string filePath)
        {
            var records = new List<Nmea2000Record>();

            using (var reader = new StreamReader(filePath))
            {
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        // Parse YDWG log line
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 3) throw new Exception("Invalid YDWG log format.");

                        // Extract timestamp, direction, and CAN ID
                        string timestamp = parts[0]; // Time of the message
                        string direction = parts[1]; // Direction (R/T)
                        string canIdHex = parts[2];  // CAN ID in hexadecimal
                        int canId = int.Parse(canIdHex, System.Globalization.NumberStyles.HexNumber);

                        // Decode fields from CAN ID
                        int priority = (canId >> 26) & 0x7;       // Top 3 bits
                        int pgn = (canId >> 8) & 0x1FFFF;         // Next 18 bits
                        int source = canId & 0xFF;                // Bottom 8 bits

                        // Determine destination for PDU1 format
                        int? destination = null;
                        if (pgn < 0xF000) // PDU1 format
                        {
                            destination = int.Parse(parts[3], System.Globalization.NumberStyles.HexNumber);
                        }

                        // Decode data bytes
                        var dataBytes = parts.Skip(3).Select(b => $"0x{b.ToUpper()}").ToList();

                        // Create and add the record
                        records.Add(new Nmea2000Record
                        {
                            Timestamp = timestamp,
                            Priority = priority.ToString(),
                            PGN = pgn.ToString(),
                            Source = source.ToString(),
                            Destination = destination?.ToString() ?? "255",
                            Data = string.Join(" ", dataBytes)
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to parse line: {line}. Error: {ex.Message}");
                    }
                }
            }

            return records;
        }
        public static List<Nmea2000Record> LoadPCANView(string filePath)
        {
            var records = new List<Nmea2000Record>();

            using (var reader = new StreamReader(filePath))
            {
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        // Match log lines with PCAN-View format
                        var regex = new Regex(@"^\s*(?<index>\d+)\)\s+(?<timeOffset>[\d.]+)\s+(?<direction>Rx|Tx)\s+(?<canId>[0-9A-F]{8})\s+(?<dlc>\d)\s+(?<data>.+)$");
                        var match = regex.Match(line);

                        if (!match.Success) continue;

                        // Extract data
                        string timeOffset = match.Groups["timeOffset"].Value;
                        string direction = match.Groups["direction"].Value;
                        string canIdHex = match.Groups["canId"].Value;
                        int dlc = int.Parse(match.Groups["dlc"].Value);
                        var dataBytes = match.Groups["data"].Value
                            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                            .Take(dlc)
                            .Select(b => $"0x{b.ToUpper()}")
                            .ToList();

                        // Decode CAN ID fields
                        int canId = int.Parse(canIdHex, NumberStyles.HexNumber);
                        int priority = (canId >> 26) & 0x7;   // Top 3 bits
                        int pgn = (canId >> 8) & 0x1FFFF;     // Middle 18 bits
                        int source = canId & 0xFF;            // Bottom 8 bits

                        // Destination (PDU1 format)
                        int? destination = null;
                        if (pgn < 0xF000) // PDU1 format
                        {
                            destination = (canId >> 8) & 0xFF;
                        }

                        // Add the record
                        records.Add(new Nmea2000Record
                        {
                            Timestamp = timeOffset,
                            Priority = priority.ToString(),
                            PGN = pgn.ToString(),
                            Source = source.ToString(),
                            Destination = destination?.ToString() ?? "255", // Default to broadcast
                            Data = string.Join(" ", dataBytes)
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to parse line: {line}. Error: {ex.Message}");
                    }
                }
            }

            return records;
        }

    }
}
