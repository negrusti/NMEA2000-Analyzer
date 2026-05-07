using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace NMEA2000Analyzer
{
    [McpServerResourceType]
    internal sealed class PacketAnalysisMcpResources
    {
        [McpServerResource(
            UriTemplate = "nmea://session/summary",
            Name = "session_summary",
            Title = "Session Summary",
            MimeType = "application/json")]
        [Description("Read-only summary of the current open data session.")]
        public static string GetSessionSummary()
        {
            return ToJson(PacketAnalysisService.GetOpenDataSummary());
        }

        [McpServerResource(
            UriTemplate = "nmea://session/current-packet",
            Name = "current_packet",
            Title = "Current Packet",
            MimeType = "application/json")]
        [Description("Read-only snapshot of the currently selected packet in the UI.")]
        public static async Task<string> GetCurrentPacket(CancellationToken cancellationToken = default)
        {
            return ToJson(await PacketAnalysisService.GetCurrentPacketAsync(cancellationToken) ?? new System.Text.Json.Nodes.JsonObject());
        }

        [McpServerResource(
            UriTemplate = "nmea://session/selected-packets",
            Name = "selected_packets",
            Title = "Selected Packets",
            MimeType = "application/json")]
        [Description("Read-only snapshot of the currently selected packets in the UI.")]
        public static async Task<string> GetSelectedPackets(CancellationToken cancellationToken = default)
        {
            return ToJson(await PacketAnalysisService.GetSelectedPacketsAsync(cancellationToken));
        }

        [McpServerResource(
            UriTemplate = "nmea://session/unknown-pgns",
            Name = "unknown_pgns",
            Title = "Unknown PGNs",
            MimeType = "application/json")]
        [Description("Read-only list of unknown or warning-producing PGNs for the assembled session data.")]
        public static string GetUnknownPgns()
        {
            return ToJson(PacketAnalysisService.ListUnknownPgns());
        }

        [McpServerResource(
            UriTemplate = "nmea://session/filters",
            Name = "session_filters",
            Title = "Session Filters",
            MimeType = "application/json")]
        [Description("Read-only snapshot of the current UI filter state.")]
        public static async Task<string> GetFilterState(CancellationToken cancellationToken = default)
        {
            return ToJson(await PacketAnalysisService.GetFilterStateAsync(cancellationToken));
        }

        [McpServerResource(
            UriTemplate = "nmea://packet/{seq}",
            Name = "packet_by_seq",
            Title = "Packet By Sequence",
            MimeType = "application/json")]
        [Description("Read a single assembled packet by sequence number.")]
        public static string GetPacketBySequence(
            [Description("Packet sequence number.")] int seq)
        {
            return ToJson(PacketAnalysisService.GetPacketBySequence(seq));
        }

        [McpServerResource(
            UriTemplate = "nmea://pgn/{pgn}/bytes",
            Name = "pgn_byte_columns",
            Title = "PGN Byte Columns",
            MimeType = "application/json")]
        [Description("Read byte-column variability for an assembled PGN.")]
        public static string GetPgnByteColumns(
            [Description("PGN to analyze.")] string pgn)
        {
            return ToJson(PacketAnalysisService.GetByteColumns(new System.Text.Json.Nodes.JsonObject
            {
                ["pgn"] = pgn,
                ["assembled"] = true
            }));
        }

        [McpServerResource(
            UriTemplate = "nmea://pgn/{pgn}/variants",
            Name = "pgn_variants",
            Title = "PGN Variants",
            MimeType = "application/json")]
        [Description("Read packet payload variants for an assembled PGN.")]
        public static string GetPgnVariants(
            [Description("PGN to analyze.")] string pgn)
        {
            return ToJson(PacketAnalysisService.GroupPacketVariants(new System.Text.Json.Nodes.JsonObject
            {
                ["pgn"] = pgn,
                ["assembled"] = true
            }));
        }

        [McpServerResource(
            UriTemplate = "nmea://packets/{device}/{pgn}/{distinct_data_only}",
            Name = "filtered_packets",
            Title = "Filtered Packets",
            MimeType = "application/json")]
        [Description("Read assembled packets filtered by device/source, PGN, and distinct-data-only mode.")]
        public static string GetFilteredPackets(
            [Description("Device identifier or source address.")] string device,
            [Description("PGN to filter.")] string pgn,
            [Description("Whether to collapse duplicate raw payloads.")] bool distinct_data_only)
        {
            return ToJson(PacketAnalysisService.QueryPacketSet(new System.Text.Json.Nodes.JsonObject
            {
                ["assembled"] = true,
                ["device"] = device,
                ["pgn"] = pgn,
                ["distinct_data_only"] = distinct_data_only
            }));
        }

        private static string ToJson(object value)
        {
            return JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
    }
}
