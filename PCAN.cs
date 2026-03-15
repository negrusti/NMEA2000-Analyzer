using Peak.Can.Basic;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using static NMEA2000Analyzer.MainWindow;

namespace NMEA2000Analyzer
{
    class PCAN
    {
        private static readonly Worker _worker = new Worker(PcanChannel.Usb01, Bitrate.Pcan250);
        private static readonly object _captureLock = new object();
        private static List<Nmea2000Record>? capture;
        private static StreamWriter? _captureWriter;
        
        public static bool StartCapture(string? captureFilePath = null)
        {
            try
            {
                capture = new List<Nmea2000Record>();

                _captureWriter?.Dispose();
                _captureWriter = null;

                if (!string.IsNullOrWhiteSpace(captureFilePath))
                {
                    var directory = Path.GetDirectoryName(captureFilePath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    _captureWriter = new StreamWriter(captureFilePath, append: false, Encoding.UTF8)
                    {
                        AutoFlush = true
                    };
                }

                _worker.MessageAvailable -= OnMessageAvailable;
                _worker.MessageAvailable += OnMessageAvailable;
                _worker.Start();
                Debug.WriteLine("Worker started successfully.");
                return (true);
            }
            catch (PcanBasicException)
            {
                _captureWriter?.Dispose();
                _captureWriter = null;
                return (false);
            }
            catch (DllNotFoundException)
            {
                _captureWriter?.Dispose();
                _captureWriter = null;
                return (false);
            }
            catch (IOException)
            {
                _captureWriter?.Dispose();
                _captureWriter = null;
                return (false);
            }
        }
        public static void StopCapture()
        {
            try
            {
                _worker.Stop();
            }
            catch (PcanBasicException)
            {
            }
            finally
            {
                _worker.MessageAvailable -= OnMessageAvailable;
                _captureWriter?.Dispose();
                _captureWriter = null;
            }
        }

        public static List<Nmea2000Record> LoadCapture()
        {
            return capture ?? new List<Nmea2000Record>();
        }

        private static void OnMessageAvailable(object? sender, MessageAvailableEventArgs e)
        {
            PcanMessage msg;
            ulong timestamp;
            while (_worker.Dequeue(out msg, out timestamp))
            {
                uint priority = (msg.ID >> 26) & 0x7;
                uint source = msg.ID & 0xFF;
                uint pgn = (msg.ID >> 8) & 0x1FFFF;
                uint destination;
                DateTime now = DateTime.Now;

                if ((pgn & 0xFF00) == 0xEF00)            // Check if it's a PDU1 (destination-specific PGN)
                {
                    destination = (pgn & 0xFF);          // Extract destination (last byte of PGN)
                    pgn &= 0x1FF00;                      // Remove destination from PGN
                }
                else
                {
                    destination = 255;                   // Broadcast for PDU2 (no destination-specific PGN)
                }

                // Format data bytes as a space-separated hex string
                byte[] data = msg.Data.Take(msg.DLC).ToArray();
                string dataHex = string.Join(" ", data.Select(b => $"0x{b:X2}"));

                var record = new Nmea2000Record
                {
                    Timestamp = now.ToString("ss.ffffff"),
                    Source = source.ToString(),
                    Destination = destination.ToString(),
                    PGN = pgn.ToString(),
                    Priority = priority.ToString(),
                    Data = dataHex,
                };

                lock (_captureLock)
                {
                    capture?.Add(record);
                    _captureWriter?.WriteLine(FormatCanDumpRecord(msg.ID, data));
                }
                // Debug.WriteLine($"TS: {timestamp}, Src: {source}, Len: {msg.DLC}, PGN: {pgn}");
            }
        }

        private static string FormatCanDumpRecord(uint canId, byte[] data)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            var timestampText = timestamp.ToString("F3", CultureInfo.InvariantCulture);
            var dataText = string.Concat(data.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
            return $"({timestampText}) can0 {canId:X8}#{dataText}";
        }

    }
}
