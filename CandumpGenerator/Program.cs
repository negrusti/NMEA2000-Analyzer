using System.Globalization;
using System.Text;

var exitCode = Run(args);
return exitCode;

static int Run(string[] args)
{
    if (args.Length == 0 || HasHelpFlag(args))
    {
        PrintUsage();
        return args.Length == 0 ? 1 : 0;
    }

    if (!TryParseSize(args[0], out var targetBytes))
    {
        Console.Error.WriteLine($"Invalid size: {args[0]}");
        PrintUsage();
        return 1;
    }

    var outputPath = args.Length > 1
        ? Path.GetFullPath(args[1])
        : Path.Combine(Directory.GetCurrentDirectory(), $"candump-test-{targetBytes}.log");

    var directory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var generator = new CandumpLogGenerator();
    var generatedBytes = generator.Generate(outputPath, targetBytes);

    Console.WriteLine($"Wrote {generatedBytes} bytes to {outputPath}");
    return 0;
}

static bool HasHelpFlag(string[] args)
{
    return args.Any(arg =>
        string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase));
}

static void PrintUsage()
{
    Console.WriteLine("Generate a synthetic NMEA 2000 candump log.");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  CandumpGenerator <size> [output-path]");
    Console.WriteLine();
    Console.WriteLine("Size examples:");
    Console.WriteLine("  256KB");
    Console.WriteLine("  10MB");
    Console.WriteLine("  1GB");
}

static bool TryParseSize(string input, out long bytes)
{
    bytes = 0;
    if (string.IsNullOrWhiteSpace(input))
    {
        return false;
    }

    var trimmed = input.Trim();
    var index = 0;
    while (index < trimmed.Length && (char.IsDigit(trimmed[index]) || trimmed[index] == '.'))
    {
        index++;
    }

    if (index == 0 || index == trimmed.Length)
    {
        return false;
    }

    if (!double.TryParse(trimmed[..index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0)
    {
        return false;
    }

    var unit = trimmed[index..].Trim().ToUpperInvariant();
    var multiplier = unit switch
    {
        "KB" => 1024d,
        "MB" => 1024d * 1024d,
        "GB" => 1024d * 1024d * 1024d,
        _ => 0d
    };

    if (multiplier == 0d)
    {
        return false;
    }

    var total = value * multiplier;
    if (total > long.MaxValue)
    {
        return false;
    }

    bytes = (long)Math.Ceiling(total);
    return true;
}

internal sealed class CandumpLogGenerator
{
    private readonly Random _random = new(2000);
    private readonly MessageTemplate[] _singleFrameTemplates;
    private readonly FastPacketTemplate[] _fastPacketTemplates;
    private readonly Dictionary<(byte Source, int Pgn), byte> _fastPacketSequences = new();
    private double _timestampSeconds = 1_710_000_000.0;

    public CandumpLogGenerator()
    {
        _singleFrameTemplates =
        [
            new MessageTemplate(127250, 28, 255, BuildVesselHeadingPayload),
            new MessageTemplate(127257, 28, 255, BuildAttitudePayload),
            new MessageTemplate(127258, 28, 255, BuildMagneticVariationPayload),
            new MessageTemplate(128259, 35, 255, BuildSpeedPayload),
            new MessageTemplate(128267, 35, 255, BuildDepthPayload),
            new MessageTemplate(129025, 40, 255, BuildPositionRapidPayload),
            new MessageTemplate(129026, 40, 255, BuildCogSogPayload),
            new MessageTemplate(130306, 50, 255, BuildWindPayload)
        ];

        _fastPacketTemplates =
        [
            new FastPacketTemplate(126996, 23, 255, BuildProductInformationPayload),
            new FastPacketTemplate(129029, 45, 255, BuildGnssPositionPayload)
        ];
    }

    public long Generate(string outputPath, long targetBytes)
    {
        long writtenBytes = 0;

        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
        while (writtenBytes < targetBytes)
        {
            foreach (var line in NextCandumpLines())
            {
                writer.WriteLine(line);
                writtenBytes += Encoding.ASCII.GetByteCount(line) + Environment.NewLine.Length;
                if (writtenBytes >= targetBytes)
                {
                    break;
                }
            }
        }

        return writtenBytes;
    }

    private IEnumerable<string> NextCandumpLines()
    {
        var useFastPacket = _random.NextDouble() < 0.18;
        if (useFastPacket)
        {
            foreach (var line in GenerateFastPacketLines(_fastPacketTemplates[_random.Next(_fastPacketTemplates.Length)]))
            {
                yield return line;
            }

            yield break;
        }

        yield return GenerateSingleFrameLine(_singleFrameTemplates[_random.Next(_singleFrameTemplates.Length)]);
    }

    private string GenerateSingleFrameLine(MessageTemplate template)
    {
        var source = (byte)_random.Next(10, 90);
        var payload = template.BuildPayload(_random);
        var canId = BuildCanId(template.Priority, template.Pgn, source, template.Destination);
        return FormatCandumpLine(canId, payload);
    }

    private IEnumerable<string> GenerateFastPacketLines(FastPacketTemplate template)
    {
        var source = (byte)_random.Next(10, 90);
        var payload = template.BuildPayload(_random);
        var sequenceKey = (source, template.Pgn);
        var sequenceId = _fastPacketSequences.TryGetValue(sequenceKey, out var current) ? current : (byte)0;
        _fastPacketSequences[sequenceKey] = (byte)((sequenceId + 1) & 0x07);

        var canId = BuildCanId(template.Priority, template.Pgn, source, template.Destination);
        var frameIndex = 0;
        var offset = 0;
        while (offset < payload.Length)
        {
            byte[] framePayload;
            if (frameIndex == 0)
            {
                var firstFrameLength = Math.Min(6, payload.Length);
                framePayload = new byte[8];
                framePayload[0] = (byte)((sequenceId << 5) | frameIndex);
                framePayload[1] = (byte)payload.Length;
                Array.Copy(payload, 0, framePayload, 2, firstFrameLength);
                offset += firstFrameLength;
            }
            else
            {
                var remaining = payload.Length - offset;
                var segmentLength = Math.Min(7, remaining);
                framePayload = new byte[8];
                framePayload[0] = (byte)((sequenceId << 5) | frameIndex);
                Array.Copy(payload, offset, framePayload, 1, segmentLength);
                offset += segmentLength;
            }

            yield return FormatCandumpLine(canId, framePayload);
            frameIndex++;
        }
    }

    private string FormatCandumpLine(uint canId, byte[] payload)
    {
        var payloadHex = Convert.ToHexString(payload);
        var line = $"({_timestampSeconds:F6}) can0 {canId:X8}#{payloadHex}";
        _timestampSeconds += 0.02 + (_random.NextDouble() * 0.03);
        return line;
    }

    private static uint BuildCanId(int priority, int pgn, byte source, byte destination)
    {
        var canPgn = pgn;
        if (pgn < 0xF000)
        {
            canPgn = (pgn & 0x1FF00) | destination;
        }

        return (uint)((priority << 26) | (canPgn << 8) | source);
    }

    private static byte[] BuildVesselHeadingPayload(Random random)
    {
        var headingRadians = random.NextDouble() * Math.PI * 2;
        var deviationRadians = (random.NextDouble() - 0.5) * 0.1;
        var variationRadians = (random.NextDouble() - 0.5) * 0.2;
        return
        [
            RandomByte(random),
            0xFF,
            ..EncodeUInt16(headingRadians / 0.0001),
            ..EncodeInt16(deviationRadians / 0.0001),
            ..EncodeInt16(variationRadians / 0.0001)
        ];
    }

    private static byte[] BuildAttitudePayload(Random random)
    {
        var yaw = (random.NextDouble() - 0.5) * Math.PI;
        var pitch = (random.NextDouble() - 0.5) * 0.5;
        var roll = (random.NextDouble() - 0.5) * 0.6;
        return
        [
            RandomByte(random),
            0xFF,
            ..EncodeInt16(yaw / 0.0001),
            ..EncodeInt16(pitch / 0.0001),
            ..EncodeInt16(roll / 0.0001)
        ];
    }

    private static byte[] BuildMagneticVariationPayload(Random random)
    {
        var daysSince1970 = random.Next(18_000, 21_000);
        var variationRadians = (random.NextDouble() - 0.5) * 0.5;
        return
        [
            0xFF,
            0xFF,
            ..EncodeUInt16(daysSince1970),
            ..EncodeInt16(variationRadians / 0.0001),
            0x00,
            0xFF
        ];
    }

    private static byte[] BuildSpeedPayload(Random random)
    {
        var waterSpeedMs = random.NextDouble() * 12;
        var speedWaterReferencedMs = Math.Max(0, waterSpeedMs + ((random.NextDouble() - 0.5) * 0.4));
        return
        [
            RandomByte(random),
            0xFF,
            ..EncodeUInt16(speedWaterReferencedMs / 0.01),
            ..EncodeUInt16(waterSpeedMs / 0.01),
            0xFD,
            0xFF
        ];
    }

    private static byte[] BuildDepthPayload(Random random)
    {
        var depthMeters = 1 + (random.NextDouble() * 80);
        var offsetMeters = (random.NextDouble() - 0.5) * 2;
        var rangeMeters = depthMeters + (random.NextDouble() * 20);
        return
        [
            RandomByte(random),
            0xFF,
            ..EncodeUInt32(depthMeters / 0.01),
            ..EncodeInt16(offsetMeters / 0.001),
            ..EncodeUInt16(rangeMeters / 0.1)
        ];
    }

    private static byte[] BuildPositionRapidPayload(Random random)
    {
        var latitude = 37.0 + (random.NextDouble() * 0.5);
        var longitude = -122.5 + (random.NextDouble() * 0.5);
        return
        [
            ..EncodeInt32(latitude * 10_000_000d),
            ..EncodeInt32(longitude * 10_000_000d)
        ];
    }

    private static byte[] BuildCogSogPayload(Random random)
    {
        var cogRadians = random.NextDouble() * Math.PI * 2;
        var sogMs = random.NextDouble() * 15;
        return
        [
            RandomByte(random),
            0xFC,
            ..EncodeUInt16(cogRadians / 0.0001),
            ..EncodeUInt16(sogMs / 0.01),
            0xFF,
            0xFF
        ];
    }

    private static byte[] BuildWindPayload(Random random)
    {
        var windSpeedMs = random.NextDouble() * 25;
        var windAngleRadians = random.NextDouble() * Math.PI * 2;
        return
        [
            RandomByte(random),
            0xFF,
            ..EncodeUInt16(windSpeedMs / 0.01),
            ..EncodeUInt16(windAngleRadians / 0.0001),
            0x02,
            0xFF
        ];
    }

    private static byte[] BuildProductInformationPayload(Random random)
    {
        var payload = new byte[26];
        Array.Copy(EncodeUInt16(1000 + random.Next(2000)), 0, payload, 0, 2);
        Array.Copy(Encoding.ASCII.GetBytes("N2K DEMO"), 0, payload, 2, 8);
        Array.Copy(Encoding.ASCII.GetBytes("FW1.0"), 0, payload, 10, 5);
        Array.Copy(Encoding.ASCII.GetBytes("MODELX"), 0, payload, 15, 6);
        Array.Copy(Encoding.ASCII.GetBytes("12345"), 0, payload, 21, 5);
        return payload;
    }

    private static byte[] BuildGnssPositionPayload(Random random)
    {
        var payload = new byte[43];
        payload[0] = RandomByte(random);
        payload[1] = 0xFF;
        Array.Copy(EncodeUInt16(random.Next(1, 365)), 0, payload, 2, 2);
        Array.Copy(EncodeUInt32((random.NextDouble() * 86_400) / 0.0001), 0, payload, 4, 4);
        Array.Copy(EncodeInt64((37.0 + random.NextDouble()) * 10_000_000_000d), 0, payload, 8, 8);
        Array.Copy(EncodeInt64((-122.0 + random.NextDouble()) * 10_000_000_000d), 0, payload, 16, 8);
        Array.Copy(EncodeInt64((5 + random.NextDouble() * 50) / 1e-6), 0, payload, 24, 8);
        Array.Copy(EncodeUInt16(250 + random.Next(200)), 0, payload, 32, 2);
        Array.Copy(EncodeUInt16(150 + random.Next(100)), 0, payload, 34, 2);
        payload[36] = 0x01;
        payload[37] = 0x0F;
        payload[38] = 0x03;
        payload[39] = 0xFF;
        payload[40] = 0xFF;
        payload[41] = 0xFF;
        payload[42] = 0xFF;
        return payload;
    }

    private static byte RandomByte(Random random) => (byte)random.Next(0, 250);

    private static byte[] EncodeUInt16(double value) => BitConverter.GetBytes((ushort)Math.Clamp(Math.Round(value), ushort.MinValue, ushort.MaxValue));
    private static byte[] EncodeUInt32(double value) => BitConverter.GetBytes((uint)Math.Clamp(Math.Round(value), uint.MinValue, uint.MaxValue));
    private static byte[] EncodeInt16(double value) => BitConverter.GetBytes((short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
    private static byte[] EncodeInt32(double value) => BitConverter.GetBytes((int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue));
    private static byte[] EncodeInt64(double value) => BitConverter.GetBytes((long)Math.Clamp(Math.Round(value), long.MinValue, long.MaxValue));

    private record MessageTemplate(int Pgn, int Priority, byte Destination, Func<Random, byte[]> BuildPayload);
    private sealed record FastPacketTemplate(int Pgn, int Priority, byte Destination, Func<Random, byte[]> BuildPayload)
        : MessageTemplate(Pgn, Priority, Destination, BuildPayload);
}
