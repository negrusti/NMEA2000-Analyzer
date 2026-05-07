using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
        private McpHttpServer? _mcpServer;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        public Canboat.Rootobject? CanboatRoot { get; set; }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var args = GetEffectiveCommandLineArgs(e.Args);

            if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var attached = AttachConsole(AttachParentProcess);
                using var cancellationSource = new CancellationTokenSource();
                ConsoleCancelEventHandler? cancelHandler = null;
                cancelHandler = (sender, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    cancellationSource.Cancel();
                };
                Console.CancelKeyPress += cancelHandler;

                try
                {
                    CanboatRoot ??= await PgnDefinitions.LoadPgnDefinitionsAsync();
                    var exitCode = await RunCommandLineModeAsync(args, cancellationSource.Token);
                    Shutdown(exitCode);
                    Environment.Exit(exitCode);
                }
                catch (OperationCanceledException)
                {
                    const int canceledExitCode = 130;
                    Console.WriteLine("Canceled.");
                    Shutdown(canceledExitCode);
                    Environment.Exit(canceledExitCode);
                }
                finally
                {
                    Console.CancelKeyPress -= cancelHandler;
                    if (attached)
                    {
                        FreeConsole();
                    }
                }

                return;
            }

            CanboatRoot ??= await PgnDefinitions.LoadPgnDefinitionsAsync();
            StartMcpServer();

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

        protected override void OnExit(ExitEventArgs e)
        {
            _mcpServer?.Dispose();
            _mcpServer = null;
            base.OnExit(e);
        }

        private void StartMcpServer()
        {
            try
            {
                var settings = McpSettings.Load();
                _mcpServer ??= new McpHttpServer(settings.Port);
                _mcpServer.Start();
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(
                        Path.Combine(AppContext.BaseDirectory, "mcp-startup-error.txt"),
                        ex.ToString());
                }
                catch
                {
                    // Ignore logging failures.
                }
            }
        }

        private static string[] GetEffectiveCommandLineArgs(string[] startupArgs)
        {
            var args = startupArgs.Length > 0
                ? startupArgs
                : Environment.GetCommandLineArgs().Skip(1).ToArray();

            if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
            {
                var envArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
                if (envArgs.Length > 0 && (envArgs[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || envArgs[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                {
                    envArgs = envArgs.Skip(1).ToArray();
                }

                if (envArgs.Length > 0 && envArgs[0].StartsWith("--", StringComparison.Ordinal))
                {
                    return envArgs;
                }
            }

            return args;
        }

        private static async Task<int> RunCommandLineModeAsync(string[] args, CancellationToken cancellationToken)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  NMEA2000Analyzer.exe --summary <file>");
                Console.WriteLine("  NMEA2000Analyzer.exe --verify <file>");
                Console.WriteLine("  NMEA2000Analyzer.exe --search-pgn <pgn> [directory]");
                return 2;
            }

            var command = args[0];
            if (command == "--search-pgn")
            {
                var directory = args.Length >= 3 ? args[2] : null;
                return await SearchPgnInWorkingDirectoryAsync(args[1], directory, cancellationToken);
            }

            var filePath = Path.GetFullPath(args[1]);

            if (!File.Exists(filePath))
            {
                Console.WriteLine("FAIL");
                Console.WriteLine($"File not found: {filePath}");
                return 1;
            }

            try
            {
                var result = await CaptureLoadService.LoadAsync(filePath, cancellationToken: cancellationToken);

                switch (command)
                {
                    case "--summary":
                        Console.WriteLine(JsonSerializer.Serialize(new
                        {
                            file = result.FilePath,
                            format = result.Format.ToString(),
                            rawCount = result.RawCount,
                            assembledCount = result.AssembledCount,
                            firstTimestamp = result.FirstTimestamp?.ToString("o"),
                            lastTimestamp = result.LastTimestamp?.ToString("o")
                        }, new JsonSerializerOptions { WriteIndented = true }));
                        return 0;

                    case "--verify":
                        var passed = result.Format != FileFormats.FileFormat.Unknown
                            && result.RawCount > 0;

                        Console.WriteLine(passed ? "OK" : "FAIL");
                        Console.WriteLine($"Format: {result.Format}");
                        Console.WriteLine($"Raw records: {result.RawCount}");
                        Console.WriteLine($"Assembled records: {result.AssembledCount}");
                        Console.WriteLine($"First timestamp: {result.FirstTimestamp?.ToString("o") ?? "<none>"}");
                        Console.WriteLine($"Last timestamp: {result.LastTimestamp?.ToString("o") ?? "<none>"}");
                        return passed ? 0 : 1;

                    default:
                        Console.WriteLine($"Unknown command: {command}");
                        return 2;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL");
                Console.WriteLine(ex.Message);
                return 1;
            }
        }

        private static async Task<int> SearchPgnInWorkingDirectoryAsync(string pgn, string? directory, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(pgn))
            {
                Console.WriteLine("FAIL");
                Console.WriteLine("PGN must not be empty.");
                return 1;
            }

            var matches = new List<string>();
            var targetDirectory = string.IsNullOrWhiteSpace(directory)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(directory);

            if (!Directory.Exists(targetDirectory))
            {
                Console.WriteLine("FAIL");
                Console.WriteLine($"Directory not found: {targetDirectory}");
                return 1;
            }

            var recognizedLogFiles = 0;

            foreach (var filePath in Directory.EnumerateFiles(targetDirectory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var format = FileFormats.DetectFileFormat(filePath);
                if (format == FileFormats.FileFormat.Unknown)
                {
                    continue;
                }

                recognizedLogFiles++;

                try
                {
                    var (_, rawRecords) = await CaptureLoadService.LoadRawAsync(filePath, cancellationToken: cancellationToken);
                    if (rawRecords.Any(record => string.Equals(record.PGN, pgn, StringComparison.Ordinal)))
                    {
                        matches.Add(Path.GetFileName(filePath));
                    }
                }
                catch
                {
                    // Skip files that are not recognized capture logs.
                }
            }

            if (recognizedLogFiles == 0)
            {
                Console.WriteLine();
                Console.WriteLine("No log files here");
                Console.WriteLine();
                return 0;
            }

            Console.WriteLine();
            foreach (var match in matches)
            {
                Console.WriteLine(match);
            }
            Console.WriteLine();

            return 0;
        }
    }
}
