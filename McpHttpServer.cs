using System.Net;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NMEA2000Analyzer
{
    internal sealed class McpHttpServer : IDisposable
    {
        private const string ProtocolVersion = "2025-03-26";
        private const string SessionHeader = "Mcp-Session-Id";
        private const string ProtocolHeader = "MCP-Protocol-Version";
        private readonly CancellationTokenSource _cancellationSource = new();
        private readonly HashSet<string> _sessionIds = new(StringComparer.Ordinal);
        private readonly int _port;
        private HttpListener? _listener;
        private Task? _acceptLoopTask;

        public McpHttpServer(int port)
        {
            _port = port;
        }

        public void Start()
        {
            if (_listener != null)
            {
                return;
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cancellationSource.Token));
        }

        public void Dispose()
        {
            _cancellationSource.Cancel();

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch
            {
                // Ignore shutdown errors.
            }

            _listener = null;
            _cancellationSource.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _listener != null)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleContextAsync(context, cancellationToken), cancellationToken);
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    if (context != null)
                    {
                        try
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            context.Response.Close();
                        }
                        catch
                        {
                            // Ignore response errors.
                        }
                    }
                }
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;

                response.Headers[ProtocolHeader] = ProtocolVersion;
                response.ContentEncoding = Encoding.UTF8;

                if (!string.Equals(request.Url?.AbsolutePath, "/mcp", StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    response.Close();
                    return;
                }

                if (string.Equals(request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = (int)HttpStatusCode.NoContent;
                    response.Headers["Allow"] = "POST, OPTIONS";
                    response.Close();
                    return;
                }

                if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    response.Headers["Allow"] = "POST, OPTIONS";
                    response.Close();
                    return;
                }

                string body;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync(cancellationToken);
                }

                if (string.IsNullOrWhiteSpace(body))
                {
                    await WriteErrorResponseAsync(response, null, -32700, "Empty request body.");
                    return;
                }

                JsonObject jsonResponse;
                string? sessionIdToReturn = null;
                try
                {
                    var jsonRequest = JsonNode.Parse(body) as JsonObject
                        ?? throw new InvalidOperationException("Invalid JSON request.");

                    var requestMethod = jsonRequest["method"]?.GetValue<string>();
                    var incomingSessionId = request.Headers[SessionHeader];

                    if (string.Equals(requestMethod, "initialize", StringComparison.Ordinal))
                    {
                        sessionIdToReturn = Guid.NewGuid().ToString("N");
                        lock (_sessionIds)
                        {
                            _sessionIds.Add(sessionIdToReturn);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(incomingSessionId))
                    {
                        lock (_sessionIds)
                        {
                            if (_sessionIds.Contains(incomingSessionId))
                            {
                                sessionIdToReturn = incomingSessionId;
                            }
                        }
                    }

                    jsonResponse = await HandleRequestAsync(jsonRequest, cancellationToken);
                }
                catch (Exception ex)
                {
                    jsonResponse = CreateErrorResponse(null, -32603, ex.Message);
                }

                if (!string.IsNullOrWhiteSpace(sessionIdToReturn))
                {
                    response.Headers[SessionHeader] = sessionIdToReturn;
                }

                await WriteJsonResponseAsync(response, jsonResponse);
            }
            catch
            {
                try
                {
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    context.Response.Close();
                }
                catch
                {
                    // Ignore response errors.
                }
            }
        }

        private async Task<JsonObject> HandleRequestAsync(JsonObject request, CancellationToken cancellationToken)
        {
            var method = request["method"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Missing method.");
            var idNode = request["id"];
            var @params = request["params"] as JsonObject ?? new JsonObject();

            return method switch
            {
                "initialize" => CreateResultResponse(idNode, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "NMEA2000Analyzer MCP",
                        ["version"] = "1.0"
                    },
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject()
                    }
                }),
                "get_server_guide" => CreateResultResponse(idNode, BuildServerGuide()),
                "tools/list" => CreateResultResponse(idNode, new JsonObject
                {
                    ["tools"] = BuildToolsList()
                }),
                "tools/call" => CreateResultResponse(idNode, await HandleToolCallAsync(@params, cancellationToken)),
                "ping" => CreateResultResponse(idNode, new JsonObject
                {
                    ["ok"] = true,
                    ["transport"] = "streamable-http",
                    ["url"] = $"http://127.0.0.1:{_port}/mcp"
                }),
                _ => CreateErrorResponse(idNode, -32601, $"Unknown method '{method}'.")
            };
        }

        private JsonObject BuildServerGuide()
        {
            return new JsonObject
            {
                ["server"] = new JsonObject
                {
                    ["name"] = "NMEA2000Analyzer MCP",
                    ["version"] = "1.0",
                    ["transport"] = "Streamable HTTP",
                    ["url"] = $"http://127.0.0.1:{_port}/mcp",
                    ["protocolVersion"] = ProtocolVersion
                },
                ["callSequence"] = new JsonArray("initialize", "get_server_guide", "tools/list", "tools/call"),
                ["conventions"] = new JsonObject
                {
                    ["openDataScope"] = "All packet-analysis tools operate on the current open data session in the app.",
                    ["openFileSync"] = "The open_file tool uses the main WPF load path and updates both the visible UI and the shared analysis session.",
                    ["pgnAndAddressTypes"] = "PGNs, source addresses, and destination addresses are represented as strings in tool arguments and results.",
                    ["assembledDefault"] = "When omitted, assembled defaults to true for analysis tools.",
                    ["uiMutatingTools"] = new JsonArray("open_file", "highlight_packets", "set_pgn_filters", "clear_filters"),
                    ["sessionHeader"] = SessionHeader
                },
                ["examples"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["purpose"] = "Open a file into the UI and active analysis session.",
                        ["request"] = new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = 1,
                            ["method"] = "tools/call",
                            ["params"] = new JsonObject
                            {
                                ["name"] = "open_file",
                                ["arguments"] = new JsonObject
                                {
                                    ["path"] = @"C:\path\to\capture.log"
                                }
                            }
                        }
                    },
                    new JsonObject
                    {
                        ["purpose"] = "Query packets for a PGN with decoded output.",
                        ["request"] = new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = 2,
                            ["method"] = "tools/call",
                            ["params"] = new JsonObject
                            {
                                ["name"] = "query_packets",
                                ["arguments"] = new JsonObject
                                {
                                    ["pgn"] = "65323",
                                    ["assembled"] = true,
                                    ["limit"] = 10,
                                    ["include_decoded"] = true
                                }
                            }
                        }
                    },
                    new JsonObject
                    {
                        ["purpose"] = "Filter the visible UI to only the PGNs of interest.",
                        ["request"] = new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = 3,
                            ["method"] = "tools/call",
                            ["params"] = new JsonObject
                            {
                                ["name"] = "set_pgn_filters",
                                ["arguments"] = new JsonObject
                                {
                                    ["include_pgns"] = new JsonArray("65323", "130845"),
                                    ["exclude_pgns"] = new JsonArray()
                                }
                            }
                        }
                    }
                }
            };
        }

        private static JsonArray BuildToolsList()
        {
            return new JsonArray(
                BuildTool(
                    "open_file",
                    "Open a log file through the main WPF load path. This updates both the visible UI and the active analysis session used by all other tools.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("path"),
                        ["properties"] = new JsonObject
                        {
                            ["path"] = BuildStringProperty("Path to the log file to load.")
                        }
                    }),
                BuildTool(
                    "get_open_data_summary",
                    "Get summary metadata for the current open data session, including file name, format, counts, and timestamp range.",
                    new JsonObject()),
                BuildTool(
                    "get_current_packet",
                    "Get the current row selection from the main UI grid. This reflects manual user selection or MCP-driven highlighting.",
                    new JsonObject()),
                BuildTool(
                    "get_selected_packets",
                    "Get all currently selected rows from the main UI grid in their current view order.",
                    new JsonObject()),
                BuildTool(
                    "list_unknown_pgns",
                    "List PGNs in the current open data session that are unknown or produce decode warnings. Assembled defaults to true when omitted.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["assembled"] = BuildBooleanProperty("Use assembled packets instead of raw packets.")
                        }
                    }),
                BuildTool(
                    "query_packets",
                    "Query packets from the current open data session with optional PGN/source/destination filters. Assembled defaults to true. Returns packets in current session order and can optionally include decoded output.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["assembled"] = BuildBooleanProperty("Use assembled packets instead of raw packets."),
                            ["pgn"] = BuildStringProperty("Filter by PGN."),
                            ["src"] = BuildStringProperty("Filter by source address."),
                            ["dst"] = BuildStringProperty("Filter by destination address."),
                            ["limit"] = BuildIntegerProperty("Maximum number of packets to return."),
                            ["offset"] = BuildIntegerProperty("Number of matching packets to skip."),
                            ["include_decoded"] = BuildBooleanProperty("Include decoded packet fields."),
                            ["only_unknown"] = BuildBooleanProperty("Return only unknown packets."),
                            ["only_with_warnings"] = BuildBooleanProperty("Return only packets with decode warnings.")
                        }
                    }),
                BuildTool(
                    "get_packet_context",
                    "Get packets immediately before and after a target sequence number within the current open data session. Assembled defaults to true.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("seq"),
                        ["properties"] = new JsonObject
                        {
                            ["seq"] = BuildIntegerProperty("Target packet sequence number."),
                            ["assembled"] = BuildBooleanProperty("Use assembled packets instead of raw packets."),
                            ["before"] = BuildIntegerProperty("How many packets before the target to return."),
                            ["after"] = BuildIntegerProperty("How many packets after the target to return.")
                        }
                    }),
                BuildTool(
                    "highlight_packets",
                    "Highlight one or more packets in the main UI grid by sequence number. This mutates the visible UI selection and switches between assembled/unassembled view if needed.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("seqs"),
                        ["properties"] = new JsonObject
                        {
                            ["seqs"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "Sequence numbers of packets to highlight.",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "integer"
                                }
                            },
                            ["assembled"] = BuildBooleanProperty("Highlight in assembled view instead of raw view.")
                        }
                    }),
                BuildTool(
                    "set_pgn_filters",
                    "Apply Include/Exclude PGN filters to the main UI grid using the same controls as the UI. Accepts arrays of strings or comma-separated strings and mutates the visible UI.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["include_pgns"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "PGNs to include in the UI filter.",
                                ["items"] = new JsonObject { ["type"] = "string" }
                            },
                            ["exclude_pgns"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "PGNs to exclude in the UI filter.",
                                ["items"] = new JsonObject { ["type"] = "string" }
                            }
                        }
                    }),
                BuildTool(
                    "clear_filters",
                    "Clear the current main-grid UI filters, including Include/Exclude PGNs, address filter, and Distinct Data.",
                    new JsonObject
                    {
                        ["type"] = "object"
                    }),
                BuildTool(
                    "get_byte_columns",
                    "Summarize byte-column variability across packets for a given PGN in the current open data session. Assembled defaults to true.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("pgn"),
                        ["properties"] = new JsonObject
                        {
                            ["pgn"] = BuildStringProperty("Target PGN."),
                            ["src"] = BuildStringProperty("Optional source-address filter."),
                            ["assembled"] = BuildBooleanProperty("Use assembled packets instead of raw packets.")
                        }
                    }),
                BuildTool(
                    "group_packet_variants",
                    "Group packets for a PGN by exact raw payload variant. Useful for discovering proprietary subtypes or state toggles. Assembled defaults to true.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("pgn"),
                        ["properties"] = new JsonObject
                        {
                            ["pgn"] = BuildStringProperty("Target PGN."),
                            ["src"] = BuildStringProperty("Optional source-address filter."),
                            ["assembled"] = BuildBooleanProperty("Use assembled packets instead of raw packets."),
                            ["limit"] = BuildIntegerProperty("Maximum number of variants to return.")
                        }
                    }),
                BuildTool(
                    "find_correlated_packets",
                    "Find packets that commonly appear near a target packet or the first matching packet of a PGN. Useful for request/response and side-effect analysis. Assembled defaults to true.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["seq"] = BuildIntegerProperty("Target packet sequence number."),
                            ["pgn"] = BuildStringProperty("Use the first matching packet of this PGN as the target."),
                            ["src"] = BuildStringProperty("Optional source-address filter when targeting by PGN."),
                            ["assembled"] = BuildBooleanProperty("Use assembled packets instead of raw packets."),
                            ["window_ms"] = BuildIntegerProperty("Time window in milliseconds."),
                            ["same_source_only"] = BuildBooleanProperty("Only include packets from the same source as the target."),
                            ["limit"] = BuildIntegerProperty("Maximum number of correlated groups to return.")
                        }
                    }),
                BuildTool(
                    "compare_packet_pair",
                    "Compare two packets byte-by-byte and return only the differing byte positions, along with packet metadata and decoded output when available. Assembled defaults to true.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("seq_a", "seq_b"),
                        ["properties"] = new JsonObject
                        {
                            ["seq_a"] = BuildIntegerProperty("First packet sequence number."),
                            ["seq_b"] = BuildIntegerProperty("Second packet sequence number."),
                            ["assembled"] = BuildBooleanProperty("Use assembled packets instead of raw packets.")
                        }
                    }));
        }

        private static async Task<JsonObject> HandleToolCallAsync(JsonObject parameters, CancellationToken cancellationToken)
        {
            var name = parameters["name"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Missing tool name.");
            var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();

            JsonNode content = name switch
            {
                "open_file" => await PacketAnalysisService.OpenFileAsync(
                    arguments["path"]?.ToString()
                        ?? throw new InvalidOperationException("Missing required argument 'path'."),
                    cancellationToken),
                "get_open_data_summary" => PacketAnalysisService.GetOpenDataSummary(),
                "get_current_packet" => await PacketAnalysisService.GetCurrentPacketAsync(cancellationToken) ?? new JsonObject(),
                "get_selected_packets" => await PacketAnalysisService.GetSelectedPacketsAsync(cancellationToken),
                "list_unknown_pgns" => PacketAnalysisService.ListUnknownPgns(arguments["assembled"]?.GetValue<bool?>() ?? true),
                "query_packets" => PacketAnalysisService.QueryPackets(arguments),
                "get_packet_context" => PacketAnalysisService.GetPacketContext(arguments),
                "highlight_packets" => await PacketAnalysisService.HighlightPacketsAsync(arguments, cancellationToken),
                "set_pgn_filters" => await PacketAnalysisService.SetPgnFiltersAsync(arguments, cancellationToken),
                "clear_filters" => await PacketAnalysisService.ClearFiltersAsync(cancellationToken),
                "get_byte_columns" => PacketAnalysisService.GetByteColumns(arguments),
                "group_packet_variants" => PacketAnalysisService.GroupPacketVariants(arguments),
                "find_correlated_packets" => PacketAnalysisService.FindCorrelatedPackets(arguments),
                "compare_packet_pair" => PacketAnalysisService.ComparePacketPair(arguments),
                _ => throw new InvalidOperationException($"Unknown tool '{name}'.")
            };

            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = content.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                    }
                },
                ["structuredContent"] = content.DeepClone()
            };
        }

        private static JsonObject BuildTool(string name, string description, JsonObject inputSchema)
        {
            return new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["inputSchema"] = inputSchema
            };
        }

        private static JsonObject BuildStringProperty(string description)
        {
            return new JsonObject
            {
                ["type"] = "string",
                ["description"] = description
            };
        }

        private static JsonObject BuildIntegerProperty(string description)
        {
            return new JsonObject
            {
                ["type"] = "integer",
                ["description"] = description
            };
        }

        private static JsonObject BuildBooleanProperty(string description)
        {
            return new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = description
            };
        }

        private static JsonObject CreateResultResponse(JsonNode? id, JsonNode result)
        {
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["result"] = result.DeepClone()
            };

            response["id"] = id?.DeepClone();
            return response;
        }

        private static JsonObject CreateErrorResponse(JsonNode? id, int code, string message)
        {
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["error"] = new JsonObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            };

            response["id"] = id?.DeepClone();
            return response;
        }

        private static async Task WriteErrorResponseAsync(HttpListenerResponse response, JsonNode? id, int code, string message)
        {
            await WriteJsonResponseAsync(response, CreateErrorResponse(id, code, message));
        }

        private static async Task WriteJsonResponseAsync(HttpListenerResponse response, JsonObject payload)
        {
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }
    }
}
