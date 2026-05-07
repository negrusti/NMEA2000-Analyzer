using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace NMEA2000Analyzer
{
    [McpServerToolType]
    internal sealed class PacketAnalysisMcpTools
    {
        [McpServerTool(Name = "open_file", Title = "Open File", OpenWorld = false, ReadOnly = false, Destructive = false, Idempotent = true)]
        [Description("Open a log file through the main WPF load path. This updates both the visible UI and the active analysis session used by all other tools.")]
        public static async Task<string> OpenFile(
            [Description("Path to the log file to load.")] string path)
        {
            return ToToolJson(await PacketAnalysisService.OpenFileAsync(path));
        }

        [McpServerTool(Name = "get_open_data_summary", Title = "Get Open Data Summary", OpenWorld = false, ReadOnly = true)]
        [Description("Get summary metadata for the current open data session, including file name, format, counts, and timestamp range.")]
        public static string GetOpenDataSummary()
        {
            return ToToolJson(PacketAnalysisService.GetOpenDataSummary());
        }

        [McpServerTool(Name = "reload_definitions", Title = "Reload Definitions", OpenWorld = false, ReadOnly = false, Destructive = false, Idempotent = true)]
        [Description("Reload merged canboat/local PGN definitions and refresh the currently open UI/session records. Use after editing local.json so new pattern matches apply without restarting the app.")]
        public static async Task<string> ReloadDefinitions()
        {
            return ToToolJson(await PacketAnalysisService.ReloadDefinitionsAsync());
        }

        [McpServerTool(Name = "get_current_packet", Title = "Get Current Packet", OpenWorld = false, ReadOnly = true)]
        [Description("Get the current row selection from the main UI grid. This reflects manual user selection or MCP-driven highlighting.")]
        public static async Task<string> GetCurrentPacket()
        {
            return ToToolJson(await PacketAnalysisService.GetCurrentPacketAsync() ?? new JsonObject());
        }

        [McpServerTool(Name = "get_selected_packets", Title = "Get Selected Packets", OpenWorld = false, ReadOnly = true)]
        [Description("Get all currently selected rows from the main UI grid in their current view order.")]
        public static async Task<string> GetSelectedPackets()
        {
            return ToToolJson(await PacketAnalysisService.GetSelectedPacketsAsync());
        }

        [McpServerTool(Name = "list_unknown_pgns", Title = "List Unknown PGNs", OpenWorld = false, ReadOnly = true)]
        [Description("List PGNs in the current open data session that are unknown or produce decode warnings. Assembled defaults to true when omitted.")]
        public static string ListUnknownPgns(
            [Description("Use assembled packets instead of raw packets.")] bool assembled = true)
        {
            return ToToolJson(PacketAnalysisService.ListUnknownPgns(assembled));
        }

        [McpServerTool(Name = "query_packets", Title = "Query Packets", OpenWorld = false, ReadOnly = true)]
        [Description("Query packets from the current open data session with optional PGN/source/destination/device filters. Assembled defaults to true. Returns packets in current session order and can optionally include decoded output. Supports distinct-data-only filtering for reverse engineering.")]
        public static string QueryPackets(
            [Description("Use assembled packets instead of raw packets.")] bool assembled = true,
            [Description("Filter by PGN.")] string? pgn = null,
            [Description("Filter by source address.")] string? src = null,
            [Description("Filter by destination address.")] string? dst = null,
            [Description("Filter by device identifier. Matches source address exactly or device info text case-insensitively.")] string? device = null,
            [Description("Maximum number of packets to return.")] int? limit = null,
            [Description("Number of matching packets to skip.")] int? offset = null,
            [Description("Include decoded packet fields.")] bool include_decoded = false,
            [Description("Return only unknown packets.")] bool only_unknown = false,
            [Description("Return only packets with decode warnings.")] bool only_with_warnings = false,
            [Description("Only return the first packet for each distinct raw data payload after filtering.")] bool distinct_data_only = false)
        {
            return ToToolJson(PacketAnalysisService.QueryPackets(BuildArguments(
                ("assembled", assembled),
                ("pgn", pgn),
                ("src", src),
                ("dst", dst),
                ("device", device),
                ("limit", limit),
                ("offset", offset),
                ("include_decoded", include_decoded),
                ("only_unknown", only_unknown),
                ("only_with_warnings", only_with_warnings),
                ("distinct_data_only", distinct_data_only))));
        }

        [McpServerTool(Name = "get_packet_context", Title = "Get Packet Context", OpenWorld = false, ReadOnly = true)]
        [Description("Get packets immediately before and after a target sequence number within the current open data session. Assembled defaults to true.")]
        public static string GetPacketContext(
            [Description("Target packet sequence number.")] int seq,
            [Description("Use assembled packets instead of raw packets.")] bool assembled = true,
            [Description("How many packets before the target to return.")] int? before = null,
            [Description("How many packets after the target to return.")] int? after = null)
        {
            return ToToolJson(PacketAnalysisService.GetPacketContext(BuildArguments(
                ("seq", seq),
                ("assembled", assembled),
                ("before", before),
                ("after", after))));
        }

        [McpServerTool(Name = "highlight_packets", Title = "Highlight Packets", OpenWorld = false, ReadOnly = false, Destructive = false, Idempotent = true)]
        [Description("Highlight one or more packets in the main UI grid by sequence number. This mutates the visible UI selection and switches between assembled/unassembled view if needed.")]
        public static async Task<string> HighlightPackets(
            [Description("Sequence numbers of packets to highlight.")] int[] seqs,
            [Description("Highlight in assembled view instead of raw view.")] bool assembled = true)
        {
            return ToToolJson(await PacketAnalysisService.HighlightPacketsAsync(BuildArguments(
                ("seqs", seqs),
                ("assembled", assembled))));
        }

        [McpServerTool(Name = "set_pgn_filters", Title = "Set PGN Filters", OpenWorld = false, ReadOnly = false, Destructive = false, Idempotent = true)]
        [Description("Apply Include/Exclude PGN filters to the main UI grid using the same controls as the UI. Accepts arrays of strings and mutates the visible UI.")]
        public static async Task<string> SetPgnFilters(
            [Description("PGNs to include in the UI filter.")] string[]? include_pgns = null,
            [Description("PGNs to exclude in the UI filter.")] string[]? exclude_pgns = null)
        {
            return ToToolJson(await PacketAnalysisService.SetPgnFiltersAsync(BuildArguments(
                ("include_pgns", include_pgns),
                ("exclude_pgns", exclude_pgns))));
        }

        [McpServerTool(Name = "clear_filters", Title = "Clear Filters", OpenWorld = false, ReadOnly = false, Destructive = false, Idempotent = true)]
        [Description("Clear the current main-grid UI filters, including Include/Exclude PGNs, address filter, and Distinct Data.")]
        public static async Task<string> ClearFilters()
        {
            return ToToolJson(await PacketAnalysisService.ClearFiltersAsync());
        }

        [McpServerTool(Name = "get_byte_columns", Title = "Get Byte Columns", OpenWorld = false, ReadOnly = true)]
        [Description("Summarize byte-column variability across packets for a given PGN in the current open data session. Assembled defaults to true.")]
        public static string GetByteColumns(
            [Description("Target PGN.")] string pgn,
            [Description("Optional source-address filter.")] string? src = null,
            [Description("Use assembled packets instead of raw packets.")] bool assembled = true)
        {
            return ToToolJson(PacketAnalysisService.GetByteColumns(BuildArguments(
                ("pgn", pgn),
                ("src", src),
                ("assembled", assembled))));
        }

        [McpServerTool(Name = "group_packet_variants", Title = "Group Packet Variants", OpenWorld = false, ReadOnly = true)]
        [Description("Group packets for a PGN by exact raw payload variant. Useful for discovering proprietary subtypes or state toggles. Assembled defaults to true.")]
        public static string GroupPacketVariants(
            [Description("Target PGN.")] string pgn,
            [Description("Optional source-address filter.")] string? src = null,
            [Description("Use assembled packets instead of raw packets.")] bool assembled = true,
            [Description("Maximum number of variants to return.")] int? limit = null)
        {
            return ToToolJson(PacketAnalysisService.GroupPacketVariants(BuildArguments(
                ("pgn", pgn),
                ("src", src),
                ("assembled", assembled),
                ("limit", limit))));
        }

        [McpServerTool(Name = "find_correlated_packets", Title = "Find Correlated Packets", OpenWorld = false, ReadOnly = true)]
        [Description("Find packets that commonly appear near a target packet or the first matching packet of a PGN. Useful for request/response and side-effect analysis. Assembled defaults to true.")]
        public static string FindCorrelatedPackets(
            [Description("Target packet sequence number.")] int? seq = null,
            [Description("Use the first matching packet of this PGN as the target.")] string? pgn = null,
            [Description("Optional source-address filter when targeting by PGN.")] string? src = null,
            [Description("Use assembled packets instead of raw packets.")] bool assembled = true,
            [Description("Time window in milliseconds.")] int? window_ms = null,
            [Description("Only include packets from the same source as the target.")] bool same_source_only = true,
            [Description("Maximum number of correlated groups to return.")] int? limit = null)
        {
            return ToToolJson(PacketAnalysisService.FindCorrelatedPackets(BuildArguments(
                ("seq", seq),
                ("pgn", pgn),
                ("src", src),
                ("assembled", assembled),
                ("window_ms", window_ms),
                ("same_source_only", same_source_only),
                ("limit", limit))));
        }

        [McpServerTool(Name = "compare_packet_pair", Title = "Compare Packet Pair", OpenWorld = false, ReadOnly = true)]
        [Description("Compare two packets byte-by-byte and return only the differing byte positions, along with packet metadata and decoded output when available. Assembled defaults to true.")]
        public static string ComparePacketPair(
            [Description("First packet sequence number.")] int seq_a,
            [Description("Second packet sequence number.")] int seq_b,
            [Description("Use assembled packets instead of raw packets.")] bool assembled = true)
        {
            return ToToolJson(PacketAnalysisService.ComparePacketPair(BuildArguments(
                ("seq_a", seq_a),
                ("seq_b", seq_b),
                ("assembled", assembled))));
        }

        [McpServerTool(Name = "get_applied_pgn_definition", Title = "Get Applied PGN Definition", OpenWorld = false, ReadOnly = true)]
        [Description("Get the exact PGN definition currently applied to a packet, using the same best-match logic as the UI Edit definition workflow.")]
        public static string GetAppliedPgnDefinition(
            [Description("Target packet sequence number.")] int seq,
            [Description("Use assembled packets instead of raw packets.")] bool assembled = true)
        {
            return ToToolJson(PacketAnalysisService.GetAppliedPgnDefinition(BuildArguments(
                ("seq", seq),
                ("assembled", assembled))));
        }

        [McpServerTool(Name = "set_applied_pgn_definition", Title = "Set Applied PGN Definition", OpenWorld = false, ReadOnly = false, Destructive = false, Idempotent = true)]
        [Description("Save a PGN definition override to local.json for the packet's applied definition match, then reload definitions. Accepts either definition_json or definition.")]
        public static async Task<string> SetAppliedPgnDefinition(
            [Description("Target packet sequence number.")] int seq,
            [Description("Definition JSON object serialized as text.")] string? definition_json = null,
            [Description("Definition JSON object as structured arguments.")] JsonObject? definition = null,
            [Description("Use assembled packets instead of raw packets.")] bool assembled = true)
        {
            return ToToolJson(await PacketAnalysisService.SetAppliedPgnDefinitionAsync(BuildArguments(
                ("seq", seq),
                ("definition_json", definition_json),
                ("definition", definition),
                ("assembled", assembled))));
        }

        [McpServerTool(Name = "clear_applied_pgn_definition", Title = "Clear Applied PGN Definition", OpenWorld = false, ReadOnly = false, Destructive = true, Idempotent = true)]
        [Description("Remove the matching PGN definition override from local.json for the packet's applied definition match, then reload definitions.")]
        public static async Task<string> ClearAppliedPgnDefinition(
            [Description("Target packet sequence number.")] int seq,
            [Description("Use assembled packets instead of raw packets.")] bool assembled = true)
        {
            return ToToolJson(await PacketAnalysisService.ClearAppliedPgnDefinitionAsync(BuildArguments(
                ("seq", seq),
                ("assembled", assembled))));
        }

        private static JsonObject BuildArguments(params (string Name, object? Value)[] values)
        {
            var result = new JsonObject();
            foreach (var (name, value) in values)
            {
                if (value == null)
                {
                    continue;
                }

                result[name] = JsonSerializer.SerializeToNode(value);
            }

            return result;
        }

        private static string ToToolJson(JsonNode node)
        {
            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
