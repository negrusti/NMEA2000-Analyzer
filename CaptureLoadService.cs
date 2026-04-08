using System.Globalization;

namespace NMEA2000Analyzer
{
    internal sealed class FileLoadProgress
    {
        public required string Stage { get; init; }
        public required string Message { get; init; }
        public double? Percent { get; init; }
    }

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
        private static void ReportProgress(IProgress<FileLoadProgress>? progress, string stage, string message, double? percent = null)
        {
            progress?.Report(new FileLoadProgress
            {
                Stage = stage,
                Message = message,
                Percent = percent
            });
        }

        public static async Task<(FileFormats.FileFormat Format, List<MainWindow.Nmea2000Record> RawRecords)> LoadRawAsync(
            string filePath,
            IProgress<FileLoadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, "Preparing", "Detecting format...", 2);
            var format = FileFormats.DetectFileFormat(filePath);
            if (format == FileFormats.FileFormat.Unknown)
            {
                throw new InvalidOperationException("Unsupported or unknown file format.");
            }

            ReportProgress(progress, "Reading File", $"{format} capture", 5);
            List<MainWindow.Nmea2000Record> rawRecords = format switch
            {
                FileFormats.FileFormat.TwoCanCsv => await Task.Run(() => FileFormats.LoadTwoCanCsv(filePath, progress), cancellationToken),
                FileFormats.FileFormat.Actisense => await Task.Run(() => FileFormats.LoadActisense(filePath, progress), cancellationToken),
                FileFormats.FileFormat.CanDump1 => await Task.Run(() => FileFormats.LoadCanDump1(filePath, progress), cancellationToken),
                FileFormats.FileFormat.CanDump2 => await Task.Run(() => FileFormats.LoadCanDump2(filePath, progress), cancellationToken),
                FileFormats.FileFormat.YDWG => await Task.Run(() => FileFormats.LoadYDWGLog(filePath, progress), cancellationToken),
                FileFormats.FileFormat.PCANView => await Task.Run(() => FileFormats.LoadPCANView(filePath, progress), cancellationToken),
                FileFormats.FileFormat.YDCsv => await Task.Run(() => FileFormats.LoadYDCsv(filePath, progress), cancellationToken),
                _ => throw new InvalidOperationException("Unsupported or unknown file format.")
            };

            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, "Reading File", $"{rawRecords.Count:N0} raw records parsed", 75);
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

        public static async Task<CaptureLoadResult> LoadAsync(string filePath, IProgress<FileLoadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var (format, rawRecords) = await LoadRawAsync(filePath, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var processingResult = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "Processing Packets", "Enriching unassembled records", 82);
                MainWindow.EnrichUnassembledRecords(rawRecords);

                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "Processing Packets", "Assembling fast packets", 89);
                var assembledRecords = MainWindow.AssembleFrames(rawRecords);
                var (firstTimestamp, lastTimestamp) = GetTimestampBounds(rawRecords);

                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "Processing Packets", "Generating device info", 95);
                PgnDefinitions.GenerateDeviceInfo(assembledRecords);

                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, "Processing Packets", "Updating device lookup tables", 98);
                MainWindow.UpdateSrcDevices(rawRecords);
                MainWindow.UpdateSrcDevices(assembledRecords);

                return new
                {
                    AssembledRecords = assembledRecords,
                    FirstTimestamp = firstTimestamp,
                    LastTimestamp = lastTimestamp
                };
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, "Ready", $"{processingResult.AssembledRecords.Count:N0} assembled records ready", 100);

            return new CaptureLoadResult
            {
                FilePath = filePath,
                Format = format,
                RawRecords = rawRecords,
                AssembledRecords = processingResult.AssembledRecords,
                FirstTimestamp = processingResult.FirstTimestamp,
                LastTimestamp = processingResult.LastTimestamp
            };
        }
    }
}
