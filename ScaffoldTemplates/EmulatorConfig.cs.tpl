using System.Text.Json;

namespace {{NamespaceName}};

public sealed class EmulatorConfig
{
    public string Channel { get; set; } = "Usb01";
    public string Bitrate { get; set; } = "Pcan250";
    public byte SourceAddress { get; set; } = {{SourceAddress}};

    public static EmulatorConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<EmulatorConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new EmulatorConfig();
    }
}
