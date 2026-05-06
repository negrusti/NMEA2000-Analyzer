namespace {{NamespaceName}};

public abstract class DeviceEmulatorBase : IDisposable
{
    private readonly List<CancellationTokenSource> _scheduleTokens = new();
    private readonly FastPacketAssembler _fastPacketAssembler = new();
    private readonly FastPacketWriter _fastPacketWriter = new();

    protected DeviceEmulatorBase(PcanBus bus, EmulatorConfig config)
    {
        Bus = bus;
        Config = config;
    }

    protected PcanBus Bus { get; }
    protected EmulatorConfig Config { get; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Bus.FrameReceived += Bus_FrameReceived;
        Bus.Start();

        try
        {
            await OnStartedAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await StopSchedulesAsync().ConfigureAwait(false);
            Bus.FrameReceived -= Bus_FrameReceived;
        }
    }

    protected virtual async Task OnStartedAsync(CancellationToken cancellationToken)
    {
        var addressClaimPayload = BuildAddressClaimPayload();
        if (addressClaimPayload.Length > 0)
        {
            await SendMessageAsync(60928, 6, 0xFF, addressClaimPayload, cancellationToken).ConfigureAwait(false);
        }

        ConfigurePeriodicMessages();
    }

    protected abstract void ConfigurePeriodicMessages();
    protected abstract bool IsFastPacketPgn(uint pgn);
    protected abstract Task HandleMessageAsync(Nmea2000Message message, CancellationToken cancellationToken);

    protected virtual byte[] BuildAddressClaimPayload()
    {
        return {{DefaultAddressClaimLiteral}};
    }

    protected void RegisterPeriodicMessage(string name, TimeSpan interval, Func<CancellationToken, Task> action)
    {
        var cts = new CancellationTokenSource();
        _scheduleTokens.Add(cts);

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
            {
                await action(cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);
    }

    protected async Task SendMessageAsync(uint pgn, byte priority, byte destinationAddress, byte[] payload, CancellationToken cancellationToken)
    {
        var canId = CanIdEncoding.Build(pgn, Config.SourceAddress, destinationAddress, priority);
        if (payload.Length <= 8)
        {
            if (!Bus.TryTransmit(new CanFrame(canId, payload, 0), out var errorMessage))
            {
                throw new InvalidOperationException(errorMessage);
            }

            return;
        }

        foreach (var framePayload in _fastPacketWriter.Split(payload))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Bus.TryTransmit(new CanFrame(canId, framePayload.ToArray(), 0), out var errorMessage))
            {
                throw new InvalidOperationException(errorMessage);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    protected static bool TryParseIsoRequest(ReadOnlySpan<byte> payload, out uint requestedPgn)
    {
        if (payload.Length < 3)
        {
            requestedPgn = 0;
            return false;
        }

        requestedPgn = payload[0] | ((uint)payload[1] << 8) | ((uint)payload[2] << 16);
        return true;
    }

    private async void Bus_FrameReceived(CanFrame frame)
    {
        try
        {
            var message = CanIdEncoding.Parse(frame);
            if (message.SourceAddress == Config.SourceAddress)
            {
                return;
            }

            if (!IsFastPacketPgn(message.Pgn))
            {
                await HandleMessageAsync(message, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (_fastPacketAssembler.TryAccept(message, out var assembledMessage) && assembledMessage != null)
            {
                await HandleMessageAsync(assembledMessage, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
        }
    }

    private async Task StopSchedulesAsync()
    {
        foreach (var cts in _scheduleTokens)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _scheduleTokens.Clear();
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        StopSchedulesAsync().GetAwaiter().GetResult();
    }
}
