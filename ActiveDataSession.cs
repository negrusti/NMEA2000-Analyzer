using System.Collections.ObjectModel;

namespace NMEA2000Analyzer
{
    internal sealed class ActiveDataSession
    {
        public required string Name { get; init; }
        public required string Format { get; init; }
        public required IReadOnlyList<MainWindow.Nmea2000Record> RawRecords { get; init; }
        public required IReadOnlyList<MainWindow.Nmea2000Record> AssembledRecords { get; init; }
        public DateTimeOffset? FirstTimestamp { get; init; }
        public DateTimeOffset? LastTimestamp { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public string? SourcePath { get; init; }
    }

    internal static class ActiveDataSessionService
    {
        private static readonly object SyncRoot = new();
        private static ActiveDataSession? _current;

        public static ActiveDataSession? GetCurrent()
        {
            lock (SyncRoot)
            {
                return _current;
            }
        }

        public static void Clear()
        {
            lock (SyncRoot)
            {
                _current = null;
            }
        }

        public static void SetCurrent(
            string name,
            string format,
            IReadOnlyList<MainWindow.Nmea2000Record> rawRecords,
            IReadOnlyList<MainWindow.Nmea2000Record> assembledRecords,
            DateTimeOffset? firstTimestamp,
            DateTimeOffset? lastTimestamp,
            string? sourcePath = null)
        {
            lock (SyncRoot)
            {
                _current = new ActiveDataSession
                {
                    Name = name,
                    Format = format,
                    RawRecords = new ReadOnlyCollection<MainWindow.Nmea2000Record>(rawRecords.ToList()),
                    AssembledRecords = new ReadOnlyCollection<MainWindow.Nmea2000Record>(assembledRecords.ToList()),
                    FirstTimestamp = firstTimestamp,
                    LastTimestamp = lastTimestamp,
                    UpdatedAt = DateTimeOffset.Now,
                    SourcePath = sourcePath
                };
            }
        }
    }
}
