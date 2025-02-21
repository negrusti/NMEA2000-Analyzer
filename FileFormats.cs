using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
            CanDump1,
            CanDump2,
            YDWG,
            YDBinary,
            YDCsv,
            PCANView
        }

        public static FileFormat DetectFileFormat(string filePath)
        {
            try
            {
                /*
                if (IsBinaryYDFormat(filePath))
                {
                    Debug.WriteLine("Binary CAN format detected");
                    return FileFormat.YDBinary;
                }
                */

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

                        if (line.StartsWith("Time,CAN,Dir,Bit,ID(hex),DLC"))
                        {
                            Debug.WriteLine("Yacht Devices CSV format detected");
                            return FileFormat.YDCsv;
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
                            return FileFormat.CanDump1;
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
                    else if (Regex.IsMatch(line, @"^\s+can\d+\s+[0-9A-F]+\s+\[\d+\]\s+([0-9A-F]{2}\s+)+"))
                    {
                        // New format: `canX  CAN_ID  [length]  data bytes`
                        Debug.WriteLine("New CAN format detected");
                        return FileFormat.CanDump2; // Add this enum value to `FileFormat`
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

        public static List<Nmea2000Record> LoadCanDump1(string filePath)
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

                    int priority = (canId >> 26) & 0x7;       // Extract bits 26-28 (3 bits)
                    int source = canId & 0xFF;               // Extract bits 0-7 (8 bits)
                    int pgn = (canId >> 8) & 0x1FFFF;        // Extract bits 8-25 (18 bits)

                    int destination;
                    if ((pgn & 0xFF00) == 0xEF00)            // Check if it's a PDU1 (destination-specific PGN)
                    {
                        destination = (pgn & 0xFF);          // Extract destination (last byte of PGN)
                        pgn &= 0x1FF00;                      // Remove destination from PGN
                    }
                    else
                    {
                        destination = 255;                   // Broadcast for PDU2 (no destination-specific PGN)
                    }

                    // Parse the CAN data
                    var data = match.Groups["data"].Value;
                    var dataFormatted = string.Join(" ", Regex.Matches(data, "..").Select(m => $"0x{m.Value}"));

                    // Create an Nmea2000Record
                    records.Add(new Nmea2000Record
                    {
                        Timestamp = datetime.ToString("o"), // ISO 8601 format
                        Source = source.ToString(),
                        Destination = destination.ToString(),
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

        public static List<Nmea2000Record> LoadCanDump2(string filePath)
        {
            var records = new List<Nmea2000Record>();

            // Regular expression to match the new log format
            var regex = new Regex(@"(?<interface>\S+)\s+(?<canId>[0-9A-F]+)\s+\[(?<length>\d+)\]\s+(?<data>([0-9A-F]{2}\s?)+)");

            foreach (var line in System.IO.File.ReadLines(filePath))
            {
                var match = regex.Match(line);
                if (!match.Success) continue;

                try
                {
                    // Parse the CAN ID
                    var canIdHex = match.Groups["canId"].Value;
                    int canId = int.Parse(canIdHex, NumberStyles.HexNumber);

                    int priority = (canId >> 26) & 0x7;       // Extract bits 26-28 (3 bits)
                    int source = canId & 0xFF;               // Extract bits 0-7 (8 bits)
                    int pgn = (canId >> 8) & 0x1FFFF;        // Extract bits 8-25 (18 bits)

                    int destination;
                    if ((pgn & 0xFF00) == 0xEF00)            // Check if it's a PDU1 (destination-specific PGN)
                    {
                        destination = (pgn & 0xFF);          // Extract destination (last byte of PGN)
                        pgn &= 0x1FF00;                      // Remove destination from PGN
                    }
                    else
                    {
                        destination = 255;                   // Broadcast for PDU2 (no destination-specific PGN)
                    }

                    // Parse the CAN data
                    var data = match.Groups["data"].Value.Trim();
                    var dataBytes = data.Split(' ')
                                        .Where(b => !string.IsNullOrEmpty(b))
                                        .Select(b => $"0x{b}");

                    // Create an Nmea2000Record
                    records.Add(new Nmea2000Record
                    {
                        Source = source.ToString(),
                        Destination = destination.ToString(),
                        PGN = pgn.ToString(),
                        Priority = priority.ToString(),
                        Data = string.Join(" ", dataBytes),
                        Description = "Unknown PGN",
                        Type = "Unknown"
                    });
                }
                catch (Exception ex)
                {
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

        private static bool IsBinaryYDFormat(string filePath)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(fs))
                {
                    if (fs.Length < 16) return false; // File must be at least one record (16 bytes) long

                    byte[] firstRecord = reader.ReadBytes(16);

                    if (firstRecord.Length < 16) return false;

                    // Check if position 4-7 (Message Identifier) is 0xFFFFFFFF (Service Record)
                    uint messageId = BitConverter.ToUInt32(firstRecord, 4);
                    if (messageId != 0xFFFFFFFF)
                        return false;

                    // Check if data field (position 8-15) contains "YDVR v05"
                    string dataString = Encoding.ASCII.GetString(firstRecord, 8, 8).TrimEnd('\0');
                    return dataString.StartsWith("YDVR v05");
                }
            }
            catch
            {
                return false;
            }
        }

        public static List<Nmea2000Record> LoadYDBinary(string filePath)
        {
            var records = new List<Nmea2000Record>();

            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(fs))
                {
                    while (reader.BaseStream.Position < reader.BaseStream.Length)
                    {
                        byte[] recordBytes = reader.ReadBytes(16);
                        if (recordBytes.Length < 16) break; // Ensure full record read

                        // Extract fields from the binary record
                        ushort header = BitConverter.ToUInt16(recordBytes, 0);
                        ushort timeInMs = BitConverter.ToUInt16(recordBytes, 2);
                        uint messageId = BitConverter.ToUInt32(recordBytes, 4);
                        byte[] data = recordBytes.Skip(8).Take(8).ToArray();

                        // Decode header fields
                        bool is11BitId = (header & 0x8000) != 0;
                        bool isTx = (header & 0x4000) != 0;
                        int dataLength = ((header >> 12) & 0x07) + 1; // Values 0-7 map to 1-8 bytes
                        int interfaceId = (header & 0x0800) != 0 ? 1 : 0;
                        int timestampMinutes = header & 0x03FF; // 10-bit timestamp in minutes

                        // Convert timestamp
                        string timestamp = $"{timestampMinutes:D4}:{timeInMs:D5}"; // Example format: "0345:01234"

                        // Extract PGN, Source, Destination
                        uint pgn;
                        int source, destination, priority;

                        if (is11BitId)
                        {
                            // 11-bit identifier case (usually not PGN-based)
                            pgn = messageId & 0x07FF;
                            source = -1;
                            destination = -1;
                            priority = -1;
                        }
                        else
                        {
                            // 29-bit identifier
                            priority = (int)((messageId >> 26) & 0x07);
                            pgn = (messageId >> 8) & 0x1FFFF; // Extract PGN (middle 18 bits)
                            destination = (int)((messageId >> 8) & 0xFF);
                            source = (int)(messageId & 0xFF);
                        }

                        if (pgn == 126996)
                            Debug.WriteLine($"Data Length: {dataLength}");
                        
                        // Format data bytes as a space-separated hex string
                        string dataHex = string.Join(" ", data.Take(dataLength).Select(b => $"0x{b:X2}"));

                        // Create the record
                        var record = new Nmea2000Record
                        {
                            Timestamp = timestamp,
                            Priority = priority.ToString(),
                            PGN = pgn.ToString(),
                            Source = source.ToString(),
                            Destination = destination.ToString(),
                            Data = dataHex
                        };

                        records.Add(record);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load binary CAN file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            return records;
        }

        public static List<Nmea2000Record> LoadYDCsv(string filePath)
        {
            var records = new List<Nmea2000Record>();

            using (var reader = new StreamReader(filePath))
            {
                string? headerLine = reader.ReadLine(); // Read the header
                if (headerLine == null) throw new Exception("File is empty.");

                // Validate header format
                var headers = headerLine.Split(',').Select(h => h.Trim()).ToArray();
                if (!headers.SequenceEqual(new[] { "Time", "CAN", "Dir", "Bit", "ID(hex)", "DLC", "D0", "D1", "D2", "D3", "D4", "D5", "D6", "D7" }))
                {
                    throw new Exception("Invalid CSV format. Expected columns: Time, CAN, Dir, Bit, ID(hex), DLC, D0-D7.");
                }

                // Read and parse the rest of the file
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var values = line.Split(',').Select(v => v.Trim()).ToArray();
                    if (values.Length < 7) continue; // Ensure there are enough columns

                    // Extract fields
                    string timestamp = values[0];
                    string canInterface = values[1];
                    string direction = values[2];
                    int bitType = int.Parse(values[3]); // 11-bit or 29-bit identifier
                    uint canId = uint.Parse(values[4], System.Globalization.NumberStyles.HexNumber);
                    int dlc = int.Parse(values[5]);

                    // Ensure DLC is within valid range (1-8)
                    if (dlc < 1 || dlc > 8) continue;

                    // Extract data bytes
                    string data = string.Join(" ", values.Skip(6).Take(dlc).Select(d => $"0x{d.ToUpper()}"));

                    // Decode CAN ID
                    int priority = -1, source = -1, destination = -1;
                    uint pgn = canId; // Default if no 29-bit conversion is needed

                    if (bitType == 29)
                    {
                        priority = (int)((canId >> 26) & 0x07);
                        pgn = (canId >> 8) & 0x1FFFF; // PGN is in bits 9-26
                        destination = (int)((canId >> 8) & 0xFF);
                        source = (int)(canId & 0xFF);
                    }

                    // Create the record
                    var record = new Nmea2000Record
                    {
                        Timestamp = timestamp,
                        Priority = priority >= 0 ? priority.ToString() : "-",
                        PGN = pgn.ToString(),
                        Source = source >= 0 ? source.ToString() : "-",
                        Destination = destination >= 0 ? destination.ToString() : "-",
                        Data = data
                    };
                    records.Add(record);
                }
            }

            return records;
        }


    }
}
