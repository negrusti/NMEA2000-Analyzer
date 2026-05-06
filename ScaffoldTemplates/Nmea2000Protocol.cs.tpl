using System.Collections.Concurrent;

namespace {{NamespaceName}};

public readonly record struct CanFrame(uint CanId, byte[] Payload, ulong TimestampTicks);

public sealed class Nmea2000Message
{
    public required uint CanId { get; init; }
    public required uint Pgn { get; init; }
    public required byte Priority { get; init; }
    public required byte SourceAddress { get; init; }
    public required byte DestinationAddress { get; init; }
    public required byte[] Payload { get; init; }
    public required bool IsDestinationSpecific { get; init; }
}

public static class CanIdEncoding
{
    public static Nmea2000Message Parse(CanFrame frame)
    {
        var priority = (byte)((frame.CanId >> 26) & 0x7);
        var source = (byte)(frame.CanId & 0xFF);
        var pgn = (frame.CanId >> 8) & 0x1FFFF;
        var pf = (pgn >> 8) & 0xFF;
        byte destination;
        var isDestinationSpecific = pf < 0xF0;
        if (isDestinationSpecific)
        {
            destination = (byte)(pgn & 0xFF);
            pgn &= 0x1FF00;
        }
        else
        {
            destination = 0xFF;
        }

        return new Nmea2000Message
        {
            CanId = frame.CanId,
            Pgn = pgn,
            Priority = priority,
            SourceAddress = source,
            DestinationAddress = destination,
            Payload = frame.Payload.ToArray(),
            IsDestinationSpecific = isDestinationSpecific
        };
    }

    public static uint Build(uint pgn, byte sourceAddress, byte destinationAddress, byte priority)
    {
        var canId = ((uint)priority & 0x7) << 26;
        var pf = (pgn >> 8) & 0xFF;
        if (pf < 0xF0)
        {
            canId |= (pgn & 0x1FF00) << 8;
            canId |= (uint)destinationAddress << 8;
        }
        else
        {
            canId |= (pgn & 0x1FFFF) << 8;
        }

        canId |= sourceAddress;
        return canId;
    }
}

public static class HexEncoding
{
    public static byte[] Parse(string hex)
    {
        return hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(token => Convert.ToByte(token, 16)).ToArray();
    }
}

public sealed class FastPacketWriter
{
    private byte _nextSequenceId;

    public IReadOnlyList<byte[]> Split(ReadOnlySpan<byte> payload)
    {
        var frames = new List<byte[]>();
        var sequenceId = _nextSequenceId++ & 0x07;
        var offset = 0;

        var firstFrame = new byte[Math.Min(8, payload.Length + 2)];
        firstFrame[0] = (byte)(sequenceId << 5);
        firstFrame[1] = (byte)payload.Length;
        var firstPayloadLength = Math.Min(6, payload.Length);
        for (var i = 0; i < firstPayloadLength; i++)
        {
            firstFrame[i + 2] = payload[i];
        }

        frames.Add(firstFrame);
        offset += firstPayloadLength;

        var frameIndex = 1;
        while (offset < payload.Length)
        {
            var framePayloadLength = Math.Min(7, payload.Length - offset);
            var frame = new byte[framePayloadLength + 1];
            frame[0] = (byte)((sequenceId << 5) | (frameIndex & 0x1F));
            for (var i = 0; i < framePayloadLength; i++)
            {
                frame[i + 1] = payload[offset + i];
            }

            frames.Add(frame);
            offset += framePayloadLength;
            frameIndex++;
        }

        return frames;
    }
}

public sealed class FastPacketAssembler
{
    private readonly ConcurrentDictionary<(uint Pgn, byte Source, byte Destination, int SequenceId), FastPacketState> _states = new();

    public bool TryAccept(Nmea2000Message frameMessage, out Nmea2000Message? assembledMessage)
    {
        assembledMessage = null;
        if (frameMessage.Payload.Length == 0)
        {
            return false;
        }

        var sequenceId = frameMessage.Payload[0] >> 5;
        var frameIndex = frameMessage.Payload[0] & 0x1F;
        var key = (frameMessage.Pgn, frameMessage.SourceAddress, frameMessage.DestinationAddress, sequenceId);

        if (frameIndex == 0)
        {
            if (frameMessage.Payload.Length < 2)
            {
                return false;
            }

            var state = new FastPacketState(frameMessage.Payload[1]);
            state.Append(frameMessage.Payload.Skip(2).ToArray());
            _states[key] = state;
        }
        else if (_states.TryGetValue(key, out var existing))
        {
            existing.Append(frameMessage.Payload.Skip(1).ToArray());
        }
        else
        {
            return false;
        }

        if (_states.TryGetValue(key, out var completed) && completed.IsComplete)
        {
            _states.TryRemove(key, out _);
            assembledMessage = new Nmea2000Message
            {
                CanId = frameMessage.CanId,
                Pgn = frameMessage.Pgn,
                Priority = frameMessage.Priority,
                SourceAddress = frameMessage.SourceAddress,
                DestinationAddress = frameMessage.DestinationAddress,
                Payload = completed.BuildPayload(),
                IsDestinationSpecific = frameMessage.IsDestinationSpecific
            };
            return true;
        }

        return false;
    }

    private sealed class FastPacketState
    {
        private readonly List<byte> _payload = new();

        public FastPacketState(int totalLength)
        {
            TotalLength = totalLength;
        }

        public int TotalLength { get; }
        public bool IsComplete => _payload.Count >= TotalLength;

        public void Append(byte[] bytes)
        {
            foreach (var byteValue in bytes)
            {
                if (_payload.Count < TotalLength)
                {
                    _payload.Add(byteValue);
                }
            }
        }

        public byte[] BuildPayload()
        {
            return _payload.Take(TotalLength).ToArray();
        }
    }
}
