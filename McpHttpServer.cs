using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NMEA2000Analyzer
{
    internal sealed class McpHttpServer : IDisposable
    {
        private static readonly object LogSync = new();
        private readonly CancellationTokenSource _cancellationSource = new();
        private readonly int _port;
        private WebApplication? _app;
        private Task? _runTask;

        public McpHttpServer(int port)
        {
            _port = port;
        }

        public void Start()
        {
            if (_app != null)
            {
                return;
            }

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(McpHttpServer).Assembly.FullName,
                ContentRootPath = AppContext.BaseDirectory
            });

            builder.WebHost.UseUrls($"http://127.0.0.1:{_port}");
            builder.Services.AddMcpServer()
                .WithHttpTransport(options =>
                {
                    options.Stateless = true;
                })
                .WithTools<PacketAnalysisMcpTools>()
                .WithResources<PacketAnalysisMcpResources>();

            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
                {
                    string? body = null;
                    if (HttpMethods.IsPost(context.Request.Method))
                    {
                        context.Request.EnableBuffering();
                        using var reader = new StreamReader(
                            context.Request.Body,
                            leaveOpen: true);
                        body = await reader.ReadToEndAsync();
                        context.Request.Body.Position = 0;

                        if (ShouldHandleJsonInitialize(context, body))
                        {
                            LogHttpMessage("request", context, body);
                            await WriteInitializeJsonResponseAsync(context.Response, body);
                            LogHttpMessage("response", context, null);
                            return;
                        }
                    }

                    LogHttpMessage("request", context, body);
                }

                await next();

                if (context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
                {
                    LogHttpMessage("response", context, null);
                }
            });
            app.MapMcp("/mcp");

            _app = app;
            app.StartAsync(_cancellationSource.Token).GetAwaiter().GetResult();
            _runTask = app.WaitForShutdownAsync(_cancellationSource.Token);
        }

        private static bool ShouldHandleJsonInitialize(HttpContext context, string? body)
        {
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                return false;
            }

            var accept = context.Request.Headers.Accept.ToString();
            if (string.IsNullOrWhiteSpace(accept)
                || !accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
                || accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.TryGetProperty("method", out var methodElement)
                    && string.Equals(methodElement.GetString(), "initialize", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static async Task WriteInitializeJsonResponseAsync(HttpResponse response, string body)
        {
            using var document = JsonDocument.Parse(body);
            JsonElement? id = document.RootElement.TryGetProperty("id", out var idElement)
                ? idElement
                : null;

            var payload = new
            {
                jsonrpc = "2.0",
                result = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new
                    {
                        logging = new { },
                        tools = new { listChanged = true },
                        resources = new
                        {
                            subscribe = false,
                            listChanged = true
                        }
                    },
                    serverInfo = new
                    {
                        name = "NMEA2000Analyzer",
                        version = "1.4.10.0"
                    }
                },
                id
            };

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "application/json";
            response.ContentLength = bytes.Length;
            await response.Body.WriteAsync(bytes);
        }

        public void Dispose()
        {
            _cancellationSource.Cancel();

            if (_app != null)
            {
                try
                {
                    _app.StopAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    // Ignore shutdown errors.
                }

                try
                {
                    _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                    // Ignore disposal errors.
                }
            }

            if (_runTask != null)
            {
                try
                {
                    _runTask.GetAwaiter().GetResult();
                }
                catch
                {
                    // Ignore run-loop shutdown errors.
                }
            }

            _app = null;
            _runTask = null;
            _cancellationSource.Dispose();
        }

        private static void LogHttpMessage(string phase, HttpContext context, string? body)
        {
            try
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, "mcp-http-log.txt");
                var lines = new List<string>
                {
                    $"[{DateTimeOffset.Now:O}] {phase.ToUpperInvariant()} {context.Request.Method} {context.Request.Path}",
                    $"Accept: {context.Request.Headers.Accept}",
                    $"Mcp-Session-Id: {context.Request.Headers["Mcp-Session-Id"]}",
                    $"StatusCode: {context.Response.StatusCode}",
                    $"ResponseContentType: {context.Response.ContentType ?? string.Empty}"
                };

                if (!string.IsNullOrWhiteSpace(body))
                {
                    lines.Add($"Body: {body}");
                }

                lines.Add(string.Empty);
                lock (LogSync)
                {
                    File.AppendAllLines(logPath, lines);
                }
            }
            catch
            {
                // Ignore diagnostic logging failures.
            }
        }
    }
}
