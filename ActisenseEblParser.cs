using System.Globalization;
using System.IO;

namespace NMEA2000Analyzer
{
    internal enum ActisenseEblDirection
    {
        Received,
        Transmitted
    }

    internal sealed record ActisenseEblFrame
    {
        public DateTime Timestamp { get; init; }
        public ActisenseEblDirection Direction { get; init; }
        public byte Priority { get; init; }
        public uint Pgn { get; init; }
        public byte Destination { get; init; }
        public byte Source { get; init; }
        public uint RelativeTimestampMs { get; init; }
        public byte[] Data { get; init; } = Array.Empty<byte>();
    }

    internal static class ActisenseEblParser
    {
        private const byte Esc = 0x1B;
        private const byte EscStart = 0x01;
        private const byte EscEnd = 0x0A;

        private const byte Dle = 0x10;
        private const byte Stx = 0x02;
        private const byte Etx = 0x03;

        private const byte CmdRx = 0x93;
        private const byte CmdTx = 0x94;
        private const byte EblTimeUtc = 0x03;

        public static List<ActisenseEblFrame> ParseFile(string path)
        {
            return Parse(File.ReadAllBytes(path));
        }

        public static List<ActisenseEblFrame> Parse(byte[] raw)
        {
            var output = new List<ActisenseEblFrame>();
            var decoded = new byte[raw.Length];
            var segments = new List<(int Start, int Length, bool IsEbl, byte EblType)>();

            var writePosition = 0;
            var segmentStart = 0;
            var escapePending = false;
            var inEblRecord = false;

            for (var i = 0; i < raw.Length; i++)
            {
                var value = raw[i];

                if (escapePending)
                {
                    escapePending = false;
                    switch (value)
                    {
                        case Esc:
                            decoded[writePosition++] = Esc;
                            break;

                        case EscStart:
                            if (!inEblRecord)
                            {
                                var nonEblLength = writePosition - segmentStart;
                                if (nonEblLength > 0)
                                {
                                    segments.Add((segmentStart, nonEblLength, false, 0));
                                }

                                segmentStart = writePosition;
                                inEblRecord = true;
                            }
                            break;

                        case EscEnd:
                            if (inEblRecord)
                            {
                                var recordLength = writePosition - segmentStart;
                                var recordType = recordLength > 0 ? decoded[segmentStart] : (byte)0;
                                segments.Add((segmentStart, recordLength, true, recordType));
                                segmentStart = writePosition;
                                inEblRecord = false;
                            }
                            break;
                    }
                }
                else if (value == Esc)
                {
                    escapePending = true;
                }
                else
                {
                    decoded[writePosition++] = value;
                }
            }

            var tailLength = writePosition - segmentStart;
            if (tailLength > 0)
            {
                segments.Add((segmentStart, tailLength, false, 0));
            }

            var hasAnchor = false;
            var anchorUtc = DateTime.MinValue;
            uint anchorRelativeMs = 0;
            var anchorRelativeSet = false;

            foreach (var segment in segments)
            {
                if (segment.IsEbl)
                {
                    if (segment.EblType == EblTimeUtc && segment.Length >= 9)
                    {
                        var fileTime = ReadUInt64LittleEndian(decoded, segment.Start + 1);
                        anchorUtc = DateTime.FromFileTimeUtc((long)fileTime);
                        hasAnchor = true;
                        anchorRelativeSet = false;
                    }

                    continue;
                }

                ParseN2kSegment(
                    decoded,
                    segment.Start,
                    segment.Length,
                    hasAnchor,
                    anchorUtc,
                    ref anchorRelativeMs,
                    ref anchorRelativeSet,
                    output);
            }

            return output;
        }

        private static void ParseN2kSegment(
            byte[] decoded,
            int start,
            int length,
            bool hasAnchor,
            DateTime anchorUtc,
            ref uint anchorRelativeMs,
            ref bool anchorRelativeSet,
            List<ActisenseEblFrame> output)
        {
            var end = start + length;
            var position = start;

            while (position < end - 1)
            {
                if (decoded[position] != Dle || decoded[position + 1] != Stx)
                {
                    position++;
                    continue;
                }

                position += 2;
                var message = new List<byte>(32);
                var dlePending = false;
                var complete = false;

                while (position < end)
                {
                    var value = decoded[position++];

                    if (dlePending)
                    {
                        dlePending = false;
                        if (value == Dle)
                        {
                            message.Add(Dle);
                        }
                        else if (value == Etx)
                        {
                            complete = true;
                            break;
                        }
                        else if (value == Stx)
                        {
                            position--;
                            break;
                        }
                    }
                    else if (value == Dle)
                    {
                        dlePending = true;
                    }
                    else
                    {
                        message.Add(value);
                    }
                }

                if (!complete || message.Count < 3 || !VerifyChecksum(message))
                {
                    continue;
                }

                var declaredLength = message[1];
                if (message.Count != 2 + declaredLength + 1)
                {
                    continue;
                }

                if (message[0] == CmdRx)
                {
                    var frame = DecodeRxFrame(message);
                    if (frame == null)
                    {
                        continue;
                    }

                    DateTime timestamp;
                    if (!hasAnchor)
                    {
                        timestamp = DateTime.MinValue;
                    }
                    else
                    {
                        if (!anchorRelativeSet)
                        {
                            anchorRelativeMs = frame.RelativeTimestampMs;
                            anchorRelativeSet = true;
                        }

                        timestamp = anchorUtc.AddMilliseconds((long)frame.RelativeTimestampMs - anchorRelativeMs);
                    }

                    output.Add(frame with { Timestamp = timestamp });
                }
                else if (message[0] == CmdTx)
                {
                    var frame = DecodeTxFrame(message);
                    if (frame != null)
                    {
                        output.Add(frame with { Timestamp = hasAnchor ? anchorUtc : DateTime.MinValue });
                    }
                }
            }
        }

        private static ActisenseEblFrame? DecodeRxFrame(List<byte> message)
        {
            if (message.Count < 14)
            {
                return null;
            }

            var dataLength = message[12];
            var dataEnd = 13 + dataLength;
            if (dataEnd > message.Count - 1)
            {
                return null;
            }

            var data = new byte[dataLength];
            for (var i = 0; i < dataLength; i++)
            {
                data[i] = message[13 + i];
            }

            return new ActisenseEblFrame
            {
                Timestamp = DateTime.MinValue,
                Direction = ActisenseEblDirection.Received,
                Priority = message[2],
                Pgn = (uint)(message[3] | (message[4] << 8) | (message[5] << 16)),
                Destination = message[6],
                Source = message[7],
                RelativeTimestampMs = (uint)(message[8] | (message[9] << 8) | (message[10] << 16) | (message[11] << 24)),
                Data = data
            };
        }

        private static ActisenseEblFrame? DecodeTxFrame(List<byte> message)
        {
            if (message.Count < 9)
            {
                return null;
            }

            var dataLength = message.Count - 9;
            var data = new byte[Math.Max(0, dataLength)];
            for (var i = 0; i < dataLength; i++)
            {
                data[i] = message[8 + i];
            }

            return new ActisenseEblFrame
            {
                Timestamp = DateTime.MinValue,
                Direction = ActisenseEblDirection.Transmitted,
                Priority = message[2],
                Pgn = (uint)(message[3] | (message[4] << 8) | (message[5] << 16)),
                Destination = message[6],
                Source = message[7],
                RelativeTimestampMs = 0,
                Data = data
            };
        }

        private static bool VerifyChecksum(List<byte> message)
        {
            byte checksum = 0;
            foreach (var value in message)
            {
                checksum += value;
            }

            return checksum == 0;
        }

        private static ulong ReadUInt64LittleEndian(byte[] buffer, int offset)
        {
            if (offset + 8 > buffer.Length)
            {
                return 0;
            }

            return (ulong)buffer[offset]
                | ((ulong)buffer[offset + 1] << 8)
                | ((ulong)buffer[offset + 2] << 16)
                | ((ulong)buffer[offset + 3] << 24)
                | ((ulong)buffer[offset + 4] << 32)
                | ((ulong)buffer[offset + 5] << 40)
                | ((ulong)buffer[offset + 6] << 48)
                | ((ulong)buffer[offset + 7] << 56);
        }

        public static string? FormatTimestamp(DateTime timestamp)
        {
            return timestamp == DateTime.MinValue
                ? null
                : timestamp.ToString("o", CultureInfo.InvariantCulture);
        }
    }
}
