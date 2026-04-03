using System.Globalization;

namespace NMEA2000Analyzer
{
    internal sealed class CaptureLoadResult
    {
        public required string FilePath { get; init; }
        public required FileFormats.FileFormat Format { get; init; }
        public required List<MainWindow.Nmea2000Record> RawRecords { get; init; }
        public required List<MainWindow.Nmea2000Record> AssembledRecords { get; init; }
        public DateTimeOffset? FirstTimestamp { get; init; }
        public DateTimeOffset? LastTimestamp { get; init; }

        public int RawCount => RawRecords.Count;
        public int AssembledCount => AssembledRecords.Count;
    }

    internal static class CaptureLoadService
    {
        public static async Task<(FileFormats.FileFormat Format, List<MainWindow.Nmea2000Record> RawRecords)> LoadRawAsync(string filePath)
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

            return (format, rawRecords);
        }

        public static (DateTimeOffset? FirstTimestamp, DateTimeOffset? LastTimestamp) GetTimestampBounds(IEnumerable<MainWindow.Nmea2000Record> records)
        {
            DateTimeOffset? firstTimestamp = null;
            DateTimeOffset? lastTimestamp = null;

            foreach (var record in records)
            {
                if (!DateTimeOffset.TryParse(record.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
                {
                    continue;
                }

                if (!firstTimestamp.HasValue || timestamp < firstTimestamp.Value)
                {
                    firstTimestamp = timestamp;
                }

                if (!lastTimestamp.HasValue || timestamp > lastTimestamp.Value)
                {
                    lastTimestamp = timestamp;
                }
            }

            return (firstTimestamp, lastTimestamp);
        }

        public static async Task<CaptureLoadResult> LoadAsync(string filePath)
        {
            var (format, rawRecords) = await LoadRawAsync(filePath);

            MainWindow.EnrichUnassembledRecords(rawRecords);
            var assembledRecords = MainWindow.AssembleFrames(rawRecords);
            var (firstTimestamp, lastTimestamp) = GetTimestampBounds(rawRecords);

            PgnDefinitions.GenerateDeviceInfo(assembledRecords);
            MainWindow.UpdateSrcDevices(rawRecords);
            MainWindow.UpdateSrcDevices(assembledRecords);

            return new CaptureLoadResult
            {
                FilePath = filePath,
                Format = format,
                RawRecords = rawRecords,
                AssembledRecords = assembledRecords,
                FirstTimestamp = firstTimestamp,
                LastTimestamp = lastTimestamp
            };
        }
    }
}
