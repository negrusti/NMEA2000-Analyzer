using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using Peak.Can.Basic;

namespace NMEA2000Analyzer
{
    class PCAN
    {
        private static readonly Worker _worker = new Worker(PcanChannel.Usb01, Bitrate.Pcan250);

        public static void InitCan()
        {
            if (_worker.Active)
            {
                _worker.Stop();
            } else
            {
                _worker.MessageAvailable += OnMessageAvailable;
                _worker.Start();
                Debug.WriteLine("Worker started successfully.");
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
                Debug.WriteLine($"TS: {timestamp}, Src: {source}, Len: {msg.DLC}, PGN: {pgn}");
            }
        }

    }
}
