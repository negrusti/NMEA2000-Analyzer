using System.Text;

namespace {{NamespaceName}};

public abstract class DeviceEmulatorBase : IDisposable
{
    private const uint IsoRequestPgn = 59904;
    private const uint AddressClaimPgn = 60928;
    private const byte MaximumClaimableAddress = 0xFB;
    private const byte GlobalAddress = 0xFF;
    private const byte NullAddress = 0xFE;

    private readonly List<CancellationTokenSource> _scheduleTokens = new();
    private readonly FastPacketAssembler _fastPacketAssembler = new();
    private readonly FastPacketWriter _fastPacketWriter = new();
    private readonly SemaphoreSlim _addressClaimSync = new(1, 1);
    private readonly object _addressSync = new();
    private readonly Lazy<DeviceIdentityConfig> _identity;
    private byte _currentSourceAddress;
    private bool _hasClaimedAddress;
    private bool _periodicMessagesConfigured;

    protected DeviceEmulatorBase(PcanBus bus, EmulatorConfig config)
    {
        Bus = bus;
        Config = config;
        _identity = new Lazy<DeviceIdentityConfig>(() =>
        {
            var identityPath = Path.IsPathRooted(config.IdentityFile)
                ? config.IdentityFile
                : Path.Combine(AppContext.BaseDirectory, config.IdentityFile);
            return DeviceIdentityConfig.Load(identityPath);
        });
        _currentSourceAddress = config.SourceAddress;
    }

    protected PcanBus Bus { get; }
    protected EmulatorConfig Config { get; }
    protected byte CurrentSourceAddress
    {
        get
        {
            lock (_addressSync)
            {
                return _currentSourceAddress;
            }
        }
    }

    protected bool HasClaimedAddress
    {
        get
        {
            lock (_addressSync)
            {
                return _hasClaimedAddress;
            }
        }
    }

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
        ValidateStartupConfiguration();
        Bus.TraceNote($"configuration validated source={Config.SourceAddress} alternates={string.Join(",", Config.AlternateSourceAddresses)}");
        await ClaimAddressAsync(Config.SourceAddress, cancellationToken).ConfigureAwait(false);
    }

    protected abstract void ConfigurePeriodicMessages();
    protected abstract bool IsFastPacketPgn(uint pgn);
    protected abstract Task HandleMessageAsync(Nmea2000Message message, CancellationToken cancellationToken);

    protected virtual byte[] BuildAddressClaimPayload()
    {
        var config = _identity.Value.AddressClaim;
        ulong name = 0;
        name |= config.UniqueNumber & 0x1FFFFFUL;
        name |= ((ulong)config.ManufacturerCode & 0x7FFUL) << 21;
        name |= ((ulong)config.DeviceInstanceLower & 0x07UL) << 32;
        name |= ((ulong)config.DeviceInstanceUpper & 0x1FUL) << 35;
        name |= ((ulong)config.DeviceFunction & 0xFFUL) << 40;
        name |= ((ulong)config.DeviceClass & 0x7FUL) << 49;
        name |= ((ulong)config.SystemInstance & 0x0FUL) << 56;
        name |= ((ulong)config.IndustryGroup & 0x07UL) << 60;
        name |= (config.ArbitraryAddressCapable ? 1UL : 0UL) << 63;

        var payload = new byte[8];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(name >> (8 * i));
        }

        return payload;
    }

    protected byte[] BuildProductInformationPayload()
    {
        var config = _identity.Value.ProductInformation;
        var payload = new byte[134];
        WriteUInt16(payload, 0, config.Nmea2000Version);
        WriteUInt16(payload, 2, config.ProductCode);
        WriteFixedAscii(payload, 4, 32, config.ModelId);
        WriteFixedAscii(payload, 36, 32, config.SoftwareVersionCode);
        WriteFixedAscii(payload, 68, 32, config.ModelVersion);
        WriteFixedAscii(payload, 100, 32, config.ModelSerialCode);
        payload[132] = config.CertificationLevel;
        payload[133] = config.LoadEquivalency;
        return payload;
    }

    protected byte[] BuildConfigurationInformationPayload()
    {
        var config = _identity.Value.ConfigurationInformation;
        return EncodeLauString(config.InstallationDescription1)
            .Concat(EncodeLauString(config.InstallationDescription2))
            .Concat(EncodeLauString(config.ManufacturerInformation))
            .ToArray();
    }

    protected void RegisterPeriodicMessage(string name, TimeSpan interval, Func<CancellationToken, Task> action)
    {
        if (!HasClaimedAddress)
        {
            throw new InvalidOperationException("Periodic NMEA 2000 messages cannot be registered before a source address is stably claimed.");
        }

        var cts = new CancellationTokenSource();
        _scheduleTokens.Add(cts);

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
            {
                if (!HasClaimedAddress)
                {
                    return;
                }

                await action(cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);
    }

    protected async Task SendMessageAsync(uint pgn, byte priority, byte destinationAddress, byte[] payload, CancellationToken cancellationToken)
    {
        if (!HasClaimedAddress && pgn != AddressClaimPgn)
        {
            throw new InvalidOperationException("Cannot transmit NMEA 2000 messages before claiming a source address.");
        }

        var canId = CanIdEncoding.Build(pgn, CurrentSourceAddress, destinationAddress, priority);
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

    private async Task ClaimAddressAsync(byte sourceAddress, CancellationToken cancellationToken)
    {
        var addressClaimPayload = BuildAddressClaimPayload();
        if (addressClaimPayload.Length != 8)
        {
            throw new InvalidOperationException("Address Claim PGN 60928 requires an 8-byte NAME payload.");
        }

        await _addressClaimSync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetAddressState(sourceAddress, hasClaimedAddress: false);
            Bus.TraceNote($"address-claim claiming address={sourceAddress} name={ReadName(addressClaimPayload):X16}");
            await SendAddressClaimAsync(sourceAddress, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, Config.AddressClaimSettleMilliseconds)), cancellationToken).ConfigureAwait(false);

            if (CurrentSourceAddress == sourceAddress && !HasClaimedAddress)
            {
                SetAddressState(sourceAddress, hasClaimedAddress: true);
                Bus.TraceNote($"address-claim claimed address={sourceAddress}");
                EnsurePeriodicMessagesConfigured();
            }
        }
        finally
        {
            _addressClaimSync.Release();
        }
    }

    private async Task HandleAddressClaimAsync(Nmea2000Message message, CancellationToken cancellationToken)
    {
        if (message.Payload.Length != 8 || message.SourceAddress != CurrentSourceAddress)
        {
            return;
        }

        var ownName = ReadName(BuildAddressClaimPayload());
        var competingName = ReadName(message.Payload);
        if (competingName == ownName)
        {
            return;
        }

        if (ownName < competingName)
        {
            Bus.TraceNote($"address-claim won address={CurrentSourceAddress} ownName={ownName:X16} competingName={competingName:X16}");
            await SendAddressClaimAsync(CurrentSourceAddress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var contestedAddress = CurrentSourceAddress;
        await StopSchedulesAsync().ConfigureAwait(false);
        SetAddressState(NullAddress, hasClaimedAddress: false);
        Bus.TraceNote($"address-claim lost address={contestedAddress} ownName={ownName:X16} competingName={competingName:X16}");

        if (TryGetNextSourceAddress(contestedAddress, out var nextAddress))
        {
            Bus.TraceNote($"address-claim fallback from={contestedAddress} to={nextAddress}");
            await ClaimAddressAsync(nextAddress, cancellationToken).ConfigureAwait(false);
            return;
        }

        SetAddressState(NullAddress, hasClaimedAddress: false);
        Bus.TraceNote($"address-claim null-address address={NullAddress}");
        await SendAddressClaimAsync(NullAddress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryHandleBaseIsoRequestAsync(Nmea2000Message message, CancellationToken cancellationToken)
    {
        if (message.Pgn != IsoRequestPgn ||
            (message.IsDestinationSpecific && message.DestinationAddress != CurrentSourceAddress) ||
            !TryParseIsoRequest(message.Payload, out var requestedPgn) ||
            requestedPgn != AddressClaimPgn)
        {
            return false;
        }

        await SendAddressClaimAsync(CurrentSourceAddress, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private Task SendAddressClaimAsync(byte sourceAddress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canId = CanIdEncoding.Build(AddressClaimPgn, sourceAddress, GlobalAddress, 6);
        if (!Bus.TryTransmit(new CanFrame(canId, BuildAddressClaimPayload(), 0), out var errorMessage))
        {
            throw new InvalidOperationException(errorMessage);
        }

        return Task.CompletedTask;
    }

    private bool TryGetNextSourceAddress(byte currentAddress, out byte nextAddress)
    {
        var candidates = Config.AlternateSourceAddresses
            .Where(address => address <= MaximumClaimableAddress && address != currentAddress)
            .Distinct()
            .ToArray();
        if (candidates.Length == 0)
        {
            nextAddress = 0;
            return false;
        }

        nextAddress = candidates[0];
        return true;
    }

    private void EnsurePeriodicMessagesConfigured()
    {
        if (_periodicMessagesConfigured)
        {
            return;
        }

        ConfigurePeriodicMessages();
        _periodicMessagesConfigured = true;
    }

    private void ValidateStartupConfiguration()
    {
        if (Config.SourceAddress > MaximumClaimableAddress)
        {
            throw new InvalidOperationException($"SourceAddress must be in the claimable range 0-{MaximumClaimableAddress}.");
        }

        if (Config.AddressClaimSettleMilliseconds < 0)
        {
            throw new InvalidOperationException("AddressClaimSettleMilliseconds cannot be negative.");
        }

        var duplicateAlternates = Config.AlternateSourceAddresses
            .GroupBy(address => address)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateAlternates.Length > 0)
        {
            throw new InvalidOperationException($"AlternateSourceAddresses contains duplicate entries: {string.Join(",", duplicateAlternates)}.");
        }

        foreach (var alternateAddress in Config.AlternateSourceAddresses)
        {
            if (alternateAddress > MaximumClaimableAddress)
            {
                throw new InvalidOperationException($"Alternate source address {alternateAddress} is outside the claimable range 0-{MaximumClaimableAddress}.");
            }

            if (alternateAddress == Config.SourceAddress)
            {
                throw new InvalidOperationException("AlternateSourceAddresses must not include SourceAddress.");
            }
        }

        ValidateIdentityConfig(_identity.Value);
    }

    private static void ValidateIdentityConfig(DeviceIdentityConfig identity)
    {
        var addressClaim = identity.AddressClaim;
        ValidateRange(addressClaim.UniqueNumber, 0x1FFFFF, "AddressClaim.UniqueNumber");
        ValidateRange(addressClaim.ManufacturerCode, 0x7FF, "AddressClaim.ManufacturerCode");
        ValidateRange(addressClaim.DeviceInstanceLower, 0x07, "AddressClaim.DeviceInstanceLower");
        ValidateRange(addressClaim.DeviceInstanceUpper, 0x1F, "AddressClaim.DeviceInstanceUpper");
        ValidateRange(addressClaim.DeviceFunction, 0xFF, "AddressClaim.DeviceFunction");
        ValidateRange(addressClaim.DeviceClass, 0x7F, "AddressClaim.DeviceClass");
        ValidateRange(addressClaim.SystemInstance, 0x0F, "AddressClaim.SystemInstance");
        ValidateRange(addressClaim.IndustryGroup, 0x07, "AddressClaim.IndustryGroup");

        var product = identity.ProductInformation;
        ValidateFixedAscii(product.ModelId, 32, "ProductInformation.ModelId");
        ValidateFixedAscii(product.SoftwareVersionCode, 32, "ProductInformation.SoftwareVersionCode");
        ValidateFixedAscii(product.ModelVersion, 32, "ProductInformation.ModelVersion");
        ValidateFixedAscii(product.ModelSerialCode, 32, "ProductInformation.ModelSerialCode");
        ValidateRange(product.CertificationLevel, 0xFF, "ProductInformation.CertificationLevel");
        ValidateRange(product.LoadEquivalency, 0xFF, "ProductInformation.LoadEquivalency");

        var configuration = identity.ConfigurationInformation;
        ValidateLauString(configuration.InstallationDescription1, "ConfigurationInformation.InstallationDescription1");
        ValidateLauString(configuration.InstallationDescription2, "ConfigurationInformation.InstallationDescription2");
        ValidateLauString(configuration.ManufacturerInformation, "ConfigurationInformation.ManufacturerInformation");
    }

    private static void ValidateRange(uint value, uint maximum, string name)
    {
        if (value > maximum)
        {
            throw new InvalidOperationException($"{name} must be in the range 0-{maximum}.");
        }
    }

    private static void ValidateFixedAscii(string? value, int length, string name)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (Encoding.ASCII.GetByteCount(value) > length)
        {
            throw new InvalidOperationException($"{name} cannot exceed {length} ASCII bytes.");
        }

        if (value.Any(character => character > 0x7F))
        {
            throw new InvalidOperationException($"{name} must contain ASCII characters only.");
        }
    }

    private static void ValidateLauString(string? value, string name)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (Encoding.UTF8.GetByteCount(value) > 251)
        {
            throw new InvalidOperationException($"{name} cannot exceed 251 UTF-8 bytes.");
        }
    }

    private void SetAddressState(byte sourceAddress, bool hasClaimedAddress)
    {
        lock (_addressSync)
        {
            _currentSourceAddress = sourceAddress;
            _hasClaimedAddress = hasClaimedAddress;
        }
    }

    private static ulong ReadName(ReadOnlySpan<byte> payload)
    {
        var value = 0UL;
        for (var i = 0; i < 8; i++)
        {
            value |= (ulong)payload[i] << (8 * i);
        }

        return value;
    }

    private static void WriteUInt16(byte[] payload, int offset, ushort value)
    {
        payload[offset] = (byte)value;
        payload[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteFixedAscii(byte[] payload, int offset, int length, string? value)
    {
        Array.Fill(payload, (byte)0x00, offset, length);
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var bytes = Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, payload, offset, Math.Min(length, bytes.Length));
    }

    private static byte[] EncodeLauString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return new byte[] { 2, 1 };
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > 251)
        {
            throw new InvalidOperationException("STRING_LAU fields cannot exceed 251 UTF-8 bytes.");
        }

        var payload = new byte[bytes.Length + 2];
        payload[0] = (byte)payload.Length;
        payload[1] = 1;
        Array.Copy(bytes, 0, payload, 2, bytes.Length);
        return payload;
    }

    private async void Bus_FrameReceived(CanFrame frame)
    {
        try
        {
            var message = CanIdEncoding.Parse(frame);
            if (message.Pgn == AddressClaimPgn)
            {
                await HandleAddressClaimAsync(message, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (message.SourceAddress == CurrentSourceAddress)
            {
                return;
            }

            if (await TryHandleBaseIsoRequestAsync(message, CancellationToken.None).ConfigureAwait(false))
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
        _periodicMessagesConfigured = false;
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        StopSchedulesAsync().GetAwaiter().GetResult();
    }
}
