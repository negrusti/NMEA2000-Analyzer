using Peak.Can.Basic;
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace NMEA2000Analyzer
{
    internal static class PcanTraceLog
    {
        private static readonly object SyncRoot = new();
        private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "pcan-trace.log");
        private static int _enabledScopeCount;

        public static string CurrentPath => LogPath;

        public static void BeginScope(string reason)
        {
            lock (SyncRoot)
            {
                _enabledScopeCount++;
            }

            LogNote($"trace begin {reason}");
        }

        public static void EndScope(string reason)
        {
            LogNote($"trace end {reason}");

            lock (SyncRoot)
            {
                if (_enabledScopeCount > 0)
                {
                    _enabledScopeCount--;
                }
            }
        }

        public static void LogIncoming(PcanMessage message)
        {
            if (!IsEnabled())
            {
                return;
            }

            var payload = new byte[message.DLC];
            for (var i = 0; i < message.DLC; i++)
            {
                payload[i] = message.Data[i];
            }

            WriteLine("pcan0-rx", message.ID, payload);
        }

        public static void LogOutgoing(uint canId, IReadOnlyList<byte> payload)
        {
            if (!IsEnabled())
            {
                return;
            }

            WriteLine("pcan0-tx", canId, payload);
        }

        public static void LogNote(string message)
        {
            if (!IsEnabledForNotes(message))
            {
                return;
            }

            lock (SyncRoot)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(
                    LogPath,
                    $"{FormatTimestamp(DateTimeOffset.Now)} note {message}{Environment.NewLine}",
                    Encoding.ASCII);
            }
        }

        private static bool IsEnabled()
        {
            lock (SyncRoot)
            {
                return _enabledScopeCount > 0;
            }
        }

        private static bool IsEnabledForNotes(string message)
        {
            if (message.StartsWith("trace begin ", StringComparison.Ordinal) ||
                message.StartsWith("trace end ", StringComparison.Ordinal))
            {
                return true;
            }

            return IsEnabled();
        }

        private static void WriteLine(string interfaceName, uint canId, IReadOnlyList<byte> payload)
        {
            var timestamp = FormatTimestamp(DateTimeOffset.Now);
            var dataHex = string.Concat(payload.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
            var line = $"{timestamp} {interfaceName} {canId:X8}#{dataHex}{Environment.NewLine}";

            lock (SyncRoot)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line, Encoding.ASCII);
            }
        }

        private static string FormatTimestamp(DateTimeOffset timestamp)
        {
            return $"({timestamp:yyyy-MM-dd HH:mm:ss.ffffffzzz})";
        }
    }
}
