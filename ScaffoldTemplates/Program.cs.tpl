using {{NamespaceName}};

var config = EmulatorConfig.Load("appsettings.json");
using var bus = new PcanBus(config.Channel, config.Bitrate);
using var emulator = new {{EmulatorClassName}}(bus, config);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"Starting {{DisplayName}} on channel {config.Channel} at {config.Bitrate}.");
Console.WriteLine("Press Ctrl+C to stop.");

try
{
    await emulator.RunAsync(cts.Token);
}
catch (Peak.Can.Basic.PcanBasicException ex) when (PcanBus.IsDeviceUnavailableError(ex.Message))
{
    Console.Error.WriteLine("PCAN device is not available. Attach the PCAN device, then try again.");
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
catch (DllNotFoundException ex)
{
    Console.Error.WriteLine("PCAN driver is not available. Install the PCAN driver/runtime, then try again.");
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
