using Peak.Can.Basic;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using static NMEA2000Analyzer.MainWindow;

namespace NMEA2000Analyzer
{
    class PCAN
    {
        private const uint IsoRequestPgn = 59904;
        private const uint ProductInformationPgn = 126996;
        private const byte BroadcastAddress = 255;
        private const byte RequestSourceAddress = 254;
        private const byte RequestPriority = 6;
        private static readonly Worker _worker = new Worker(PcanChannel.Usb01, Bitrate.Pcan250);
        private static List<Nmea2000Record>? capture;
        private static int capturedCount;
        
        public static bool StartCapture()
        {
            try
            {
                if (capture == null)
                {
                    capture = new List<Nmea2000Record>();
                }
                else
                {
                    capture.Clear();
                }

                capturedCount = 0;
                _worker.MessageAvailable -= OnMessageAvailable;
                _worker.MessageAvailable += OnMessageAvailable;
                _worker.Start();
                Debug.WriteLine("Worker started successfully.");
                return (true);
            }
            catch (PcanBasicException)
            {
                return (false);
            }
            catch (DllNotFoundException)
            {
                return (false);
            }
        }
        public static void StopCapture()
        {
            try
            {
                _worker.Stop();
                _worker.MessageAvailable -= OnMessageAvailable;
            }
            catch (PcanBasicException e)
            {
            }
        }

        public static List<Nmea2000Record> LoadCapture()
        {
            return (capture);
        }

        public static int GetCapturedCount()
        {
            return Volatile.Read(ref capturedCount);
        }

        public static bool RequestProductInformationBroadcast(out string errorMessage)
        {
            try
            {
                var requestCanId = BuildCanId(IsoRequestPgn, BroadcastAddress, RequestSourceAddress, RequestPriority);
                var requestedPgnBytes = new byte[]
                {
                    (byte)(ProductInformationPgn & 0xFF),
                    (byte)((ProductInformationPgn >> 8) & 0xFF),
                    (byte)((ProductInformationPgn >> 16) & 0xFF)
                };

                var message = new PcanMessage(
                    requestCanId,
                    MessageType.Extended,
                    (byte)requestedPgnBytes.Length,
                    requestedPgnBytes,
                    false);

                if (_worker.Transmit(message, out var error))
                {
                    errorMessage = string.Empty;
                    return true;
                }

                errorMessage = $"Failed to send request: {error}.";
                return false;
            }
            catch (PcanBasicException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
            catch (DllNotFoundException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
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
                var now = DateTimeOffset.Now;

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
                byte[] data = msg.Data;
                string dataHex = string.Join(" ", data.Select(b => $"0x{b:X2}"));

                capture.Add(new Nmea2000Record
                {
                    Timestamp = now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                    Source = source.ToString(),
                    Destination = destination.ToString(),
                    PGN = pgn.ToString(),
                    Priority = priority.ToString(),
                    Data = dataHex,
                });
                Interlocked.Increment(ref capturedCount);
                // Debug.WriteLine($"TS: {timestamp}, Src: {source}, Len: {msg.DLC}, PGN: {pgn}");
            }
        }

        private static uint BuildCanId(uint pgn, byte destination, byte source, byte priority)
        {
            var pgnField = pgn;
            if (pgnField < 0xF000)
            {
                pgnField |= destination;
            }

            return ((uint)priority << 26) | (pgnField << 8) | source;
        }

    }
}
