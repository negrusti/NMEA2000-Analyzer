using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace NMEA2000Analyzer
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private const int AttachParentProcess = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        public Canboat.Rootobject? CanboatRoot { get; set; }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var args = e.Args.Length > 0
                ? e.Args
                : Environment.GetCommandLineArgs().Skip(1).ToArray();

            if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var attached = AttachConsole(AttachParentProcess);

                try
                {
                    CanboatRoot ??= await PgnDefinitions.LoadPgnDefinitionsAsync();
                    var exitCode = await RunCommandLineModeAsync(args);
                    Shutdown(exitCode);
                    Environment.Exit(exitCode);
                }
                finally
                {
                    if (attached)
                    {
                        FreeConsole();
                    }
                }

                return;
            }

            CanboatRoot ??= await PgnDefinitions.LoadPgnDefinitionsAsync();

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();

            if (args.Length == 0)
            {
                return;
            }

            var filePath = args[0];
            if (!File.Exists(filePath))
            {
                MessageBox.Show($"File not found: {filePath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            await mainWindow.LoadFileFromCommandLineAsync(filePath);
        }

        private static async Task<int> RunCommandLineModeAsync(string[] args)
        {
            static void WriteCliTrace(params string[] lines)
            {
                try
                {
                    var tracePath = Path.Combine(AppContext.BaseDirectory, "cli-last-run.txt");
                    File.WriteAllLines(tracePath, lines);
                }
                catch
                {
                    // Best-effort diagnostics only.
                }
            }

            if (args.Length < 2)
            {
                WriteCliTrace("Usage error", string.Join(" ", args));
                Console.WriteLine("Usage:");
                Console.WriteLine("  NMEA2000Analyzer.exe --summary <file>");
                Console.WriteLine("  NMEA2000Analyzer.exe --verify <file>");
                return 2;
            }

            var command = args[0];
            var filePath = Path.GetFullPath(args[1]);

            if (!File.Exists(filePath))
            {
                WriteCliTrace("File not found", filePath);
                Console.WriteLine("FAIL");
                Console.WriteLine($"File not found: {filePath}");
                return 1;
            }

            try
            {
                var result = await CaptureLoadService.LoadAsync(filePath);

                switch (command)
                {
                    case "--summary":
                        WriteCliTrace(
                            "OK",
                            $"Command: {command}",
                            $"File: {result.FilePath}",
                            $"Format: {result.Format}",
                            $"Raw records: {result.RawCount}",
                            $"Assembled records: {result.AssembledCount}");
                        Console.WriteLine(JsonSerializer.Serialize(new
                        {
                            file = result.FilePath,
                            format = result.Format.ToString(),
                            rawCount = result.RawCount,
                            assembledCount = result.AssembledCount,
                            firstTimestamp = result.FirstTimestamp,
                            lastTimestamp = result.LastTimestamp
                        }, new JsonSerializerOptions { WriteIndented = true }));
                        return 0;

                    case "--verify":
                        var passed = result.Format != FileFormats.FileFormat.Unknown
                            && result.RawCount > 0;

                        WriteCliTrace(
                            passed ? "OK" : "FAIL",
                            $"Command: {command}",
                            $"File: {result.FilePath}",
                            $"Format: {result.Format}",
                            $"Raw records: {result.RawCount}",
                            $"Assembled records: {result.AssembledCount}",
                            $"First timestamp: {result.FirstTimestamp ?? "<none>"}",
                            $"Last timestamp: {result.LastTimestamp ?? "<none>"}");
                        Console.WriteLine(passed ? "OK" : "FAIL");
                        Console.WriteLine($"Format: {result.Format}");
                        Console.WriteLine($"Raw records: {result.RawCount}");
                        Console.WriteLine($"Assembled records: {result.AssembledCount}");
                        Console.WriteLine($"First timestamp: {result.FirstTimestamp ?? "<none>"}");
                        Console.WriteLine($"Last timestamp: {result.LastTimestamp ?? "<none>"}");
                        return passed ? 0 : 1;

                    default:
                        WriteCliTrace("Unknown command", command, filePath);
                        Console.WriteLine($"Unknown command: {command}");
                        return 2;
                }
            }
            catch (Exception ex)
            {
                WriteCliTrace(
                    "FAIL",
                    $"Command: {command}",
                    $"File: {filePath}",
                    ex.ToString());
                Console.WriteLine("FAIL");
                Console.WriteLine(ex.Message);
                return 1;
            }
        }
    }
}
