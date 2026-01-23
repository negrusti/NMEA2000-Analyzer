using Peak.Can.Basic;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using static NMEA2000Analyzer.MainWindow;

namespace NMEA2000Analyzer
{
    class PCAN
    {
        private static readonly Worker _worker = new Worker(PcanChannel.Usb01, Bitrate.Pcan250);
        private static List<Nmea2000Record>? capture;
        
        public static void InitCan(Boolean start)
        {
            if ((start))
            {
                if (capture == null)
                { 
                    capture = new List<Nmea2000Record>();
                }
                _worker.MessageAvailable += OnMessageAvailable;
                _worker.Start();
                Debug.WriteLine("Worker started successfully.");
            }
            else
            {
                _worker.Stop();
            }
        }

        public static List<Nmea2000Record> LoadCapture()
        {
            return (capture);
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
                byte[] data = msg.Data;
                string dataHex = string.Join(" ", data.Select(b => $"0x{b:X2}"));

                capture.Add(new Nmea2000Record
                {
                    Timestamp = now.ToString("ss.ffffff"),
                    Source = source.ToString(),
                    Destination = destination.ToString(),
                    PGN = pgn.ToString(),
                    Priority = priority.ToString(),
                    Data = dataHex,
                });
                // Debug.WriteLine($"TS: {timestamp}, Src: {source}, Len: {msg.DLC}, PGN: {pgn}");
            }
        }

    }
}
