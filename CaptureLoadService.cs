using System.Globalization;

namespace NMEA2000Analyzer
{
    internal sealed class CaptureLoadResult
    {
        public required string FilePath { get; init; }
        public required FileFormats.FileFormat Format { get; init; }
        public required List<MainWindow.Nmea2000Record> RawRecords { get; init; }
        public required List<MainWindow.Nmea2000Record> AssembledRecords { get; init; }

        public int RawCount => RawRecords.Count;
        public int AssembledCount => AssembledRecords.Count;
        public string? FirstTimestamp => GetBoundaryTimestamp(timestamps => timestamps.Min());
        public string? LastTimestamp => GetBoundaryTimestamp(timestamps => timestamps.Max());

        private string? GetBoundaryTimestamp(Func<List<DateTimeOffset>, DateTimeOffset> selector)
        {
            var timestamps = RawRecords
                .Select(record => DateTimeOffset.TryParse(record.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
                    ? timestamp
                    : (DateTimeOffset?)null)
                .Where(timestamp => timestamp.HasValue)
                .Select(timestamp => timestamp!.Value)
                .ToList();

            return timestamps.Count == 0 ? null : selector(timestamps).ToString("o");
        }
    }

    internal static class CaptureLoadService
    {
        public static async Task<CaptureLoadResult> LoadAsync(string filePath)
        {
            var format = FileFormats.DetectFileFormat(filePath);
            if (format == FileFormats.FileFormat.Unknown)
            {
                throw new InvalidOperationException("Unsupported or unknown file format.");
            }

            List<MainWindow.Nmea2000Record> rawRecords = format switch
            {
                FileFormats.FileFormat.TwoCanCsv => await Task.Run(() => FileFormats.LoadTwoCanCsv(filePath)),
                FileFormats.FileFormat.Actisense => await Task.Run(() => FileFormats.LoadActisense(filePath)),
                FileFormats.FileFormat.CanDump1 => await Task.Run(() => FileFormats.LoadCanDump1(filePath)),
                FileFormats.FileFormat.CanDump2 => await Task.Run(() => FileFormats.LoadCanDump2(filePath)),
                FileFormats.FileFormat.YDWG => await Task.Run(() => FileFormats.LoadYDWGLog(filePath)),
                FileFormats.FileFormat.PCANView => await Task.Run(() => FileFormats.LoadPCANView(filePath)),
                FileFormats.FileFormat.YDCsv => await Task.Run(() => FileFormats.LoadYDCsv(filePath)),
                _ => throw new InvalidOperationException("Unsupported or unknown file format.")
            };

            MainWindow.EnrichUnassembledRecords(rawRecords);
            var assembledRecords = MainWindow.AssembleFrames(rawRecords);

            PgnDefinitions.GenerateDeviceInfo(assembledRecords);
            MainWindow.UpdateSrcDevices(rawRecords);
            MainWindow.UpdateSrcDevices(assembledRecords);

            return new CaptureLoadResult
            {
                FilePath = filePath,
                Format = format,
                RawRecords = rawRecords,
                AssembledRecords = assembledRecords
            };
        }
    }
}
