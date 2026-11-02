using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace NMEA2000Analyzer
{
    internal sealed class McpHttpServer : IDisposable
    {
        private static readonly object LogSync = new();
        private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ToolArgumentAllowLists =
            BuildToolArgumentAllowLists();
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

                        if (TryBuildToolInputValidationError(context, body, out var errorPayload))
                        {
                            LogHttpMessage("request", context, body);
                            await WriteJsonRpcValidationErrorAsync(context, errorPayload);
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

        private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildToolArgumentAllowLists()
        {
            return typeof(PacketAnalysisMcpTools)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => new
                {
                    Method = method,
                    Attribute = method.GetCustomAttribute<McpServerToolAttribute>()
                })
                .Where(item => item.Attribute != null)
                .ToDictionary(
                    item => item.Attribute!.Name ?? item.Method.Name,
                    item => (IReadOnlySet<string>)new HashSet<string>(
                        item.Method
                            .GetParameters()
                            .Select(parameter => parameter.Name)
                            .Where(name => !string.IsNullOrWhiteSpace(name))
                            .Cast<string>(),
                        StringComparer.Ordinal),
                    StringComparer.Ordinal);
        }

        private static bool TryBuildToolInputValidationError(
            HttpContext context,
            string? body,
            out JsonObject errorPayload)
        {
            errorPayload = new JsonObject();
            if (!HttpMethods.IsPost(context.Request.Method) || string.IsNullOrWhiteSpace(body))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("method", out var methodElement)
                    || !string.Equals(methodElement.GetString(), "tools/call", StringComparison.Ordinal)
                    || !root.TryGetProperty("params", out var paramsElement)
                    || paramsElement.ValueKind != JsonValueKind.Object
                    || !paramsElement.TryGetProperty("name", out var nameElement))
                {
                    return false;
                }

                var toolName = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(toolName)
                    || !ToolArgumentAllowLists.TryGetValue(toolName, out var allowedArguments)
                    || !paramsElement.TryGetProperty("arguments", out var argumentsElement)
                    || argumentsElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var unknownArguments = argumentsElement
                    .EnumerateObject()
                    .Select(property => property.Name)
                    .Where(name => !allowedArguments.Contains(name))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();

                if (unknownArguments.Count == 0)
                {
                    return false;
                }

                JsonNode? id = root.TryGetProperty("id", out var idElement)
                    ? JsonNode.Parse(idElement.GetRawText())
                    : null;

                errorPayload = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32602,
                        ["message"] = $"InputValidationError: Unknown argument(s): {string.Join(", ", unknownArguments)}."
                    }
                };
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static async Task WriteJsonRpcValidationErrorAsync(HttpContext context, JsonObject payload)
        {
            var json = payload.ToJsonString();
            context.Response.StatusCode = StatusCodes.Status200OK;

            var accept = context.Request.Headers.Accept.ToString();
            if (accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync($"event: message\ndata: {json}\n\n", Encoding.UTF8);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes);
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
            var version = Assembly.GetExecutingAssembly().GetName().Version;

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
                        version = version?.ToString(4) ?? "0.0.0.0"
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
