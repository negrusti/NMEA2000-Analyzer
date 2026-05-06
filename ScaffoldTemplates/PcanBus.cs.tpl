using Peak.Can.Basic;
using System.Globalization;

namespace {{NamespaceName}};

public sealed class PcanBus : IDisposable
{
    private readonly Worker _worker;
    private readonly string _traceFilePath;
    private readonly object _traceSync = new();
    private bool _started;

    public PcanBus(string channelName, string bitrateName)
    {
        var channel = Enum.Parse<PcanChannel>(channelName, ignoreCase: true);
        var bitrate = Enum.Parse<Bitrate>(bitrateName, ignoreCase: true);
        _worker = new Worker(channel, bitrate);
        var logsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logsDirectory);
        _traceFilePath = Path.Combine(logsDirectory, "tracer.log");
        File.WriteAllText(_traceFilePath, string.Empty);
        WriteTraceNote($"trace start channel={channelName} bitrate={bitrateName}");
    }

    public event Action<CanFrame>? FrameReceived;

    public static bool IsDeviceUnavailableError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        return errorMessage.Contains("The value of a handle", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("PCAN-Channel", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("PCAN-Hardware", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("PCAN-Net", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("PCAN-Client", StringComparison.OrdinalIgnoreCase);
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _worker.MessageAvailable += Worker_MessageAvailable;
        try
        {
            _worker.Start();
            _started = true;
        }
        catch (Exception ex)
        {
            WriteTraceNote($"start failed error={ex.Message}");
            _worker.MessageAvailable -= Worker_MessageAvailable;
            throw;
        }
    }

    public bool TryTransmit(CanFrame frame, out string? errorMessage)
    {
        var message = new PcanMessage(
            frame.CanId,
            MessageType.Extended,
            (byte)frame.Payload.Length,
            frame.Payload.ToArray(),
            false);

        if (_worker.Transmit(message, out var error))
        {
            WriteTraceFrame("tx", frame.CanId, frame.Payload);
            errorMessage = null;
            return true;
        }

        WriteTraceNote($"tx failed canId={frame.CanId:X8} error={error}");
        errorMessage = error.ToString();
        return false;
    }

    private void Worker_MessageAvailable(object? sender, MessageAvailableEventArgs e)
    {
        while (_worker.Dequeue(out var message, out var timestamp))
        {
            var payload = new byte[message.Length];
            for (var i = 0; i < message.Length; i++)
            {
                payload[i] = message.Data[i];
            }

            WriteTraceFrame("rx", message.ID, payload);
            FrameReceived?.Invoke(new CanFrame(message.ID, payload, timestamp));
        }
    }

    public void Dispose()
    {
        if (_started)
        {
            _worker.MessageAvailable -= Worker_MessageAvailable;
            _worker.Stop();
            _started = false;
        }

        WriteTraceNote("trace end");
    }

    private void WriteTraceFrame(string direction, uint canId, ReadOnlySpan<byte> payload)
    {
        var payloadHex = Convert.ToHexString(payload);
        WriteTraceLine($"({DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture)}) pcan0-{direction} {canId:X8}#{payloadHex}");
    }

    private void WriteTraceNote(string text)
    {
        WriteTraceLine($"({DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture)}) note {text}");
    }

    private void WriteTraceLine(string line)
    {
        lock (_traceSync)
        {
            File.AppendAllText(_traceFilePath, line + Environment.NewLine);
        }
    }
}
