using System.IO;
using System.Text.Json;

namespace NMEA2000Analyzer
{
    internal sealed class McpSettings
    {
        public const int DefaultPort = 48765;

        public int Port { get; set; } = DefaultPort;

        public static McpSettings Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "mcp.json");
            if (!File.Exists(path))
            {
                return new McpSettings();
            }

            try
            {
                var settings = JsonSerializer.Deserialize<McpSettings>(File.ReadAllText(path), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (settings == null || settings.Port < 10000 || settings.Port > 65535)
                {
                    return new McpSettings();
                }

                return settings;
            }
            catch
            {
                return new McpSettings();
            }
        }
    }
}
