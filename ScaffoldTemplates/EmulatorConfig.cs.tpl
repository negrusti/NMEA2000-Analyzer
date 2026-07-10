using System.Text.Json;

namespace {{NamespaceName}};

public sealed class EmulatorConfig
{
    public string Channel { get; set; } = "Usb01";
    public string Bitrate { get; set; } = "Pcan250";
    public byte SourceAddress { get; set; } = {{SourceAddress}};
    public byte[] AlternateSourceAddresses { get; set; } = Array.Empty<byte>();
    public int AddressClaimSettleMilliseconds { get; set; } = 250;
    public string IdentityFile { get; set; } = "device-identity.json";

    public static EmulatorConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<EmulatorConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new EmulatorConfig();
    }
}

public sealed class DeviceIdentityConfig
{
    public AddressClaimConfig AddressClaim { get; set; } = new();
    public ProductInformationConfig ProductInformation { get; set; } = new();
    public ConfigurationInformationConfig ConfigurationInformation { get; set; } = new();

    public static DeviceIdentityConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new DeviceIdentityConfig();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<DeviceIdentityConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new DeviceIdentityConfig();
    }
}

public sealed class AddressClaimConfig
{
    public uint UniqueNumber { get; set; }
    public ushort ManufacturerCode { get; set; }
    public byte DeviceInstanceLower { get; set; }
    public byte DeviceInstanceUpper { get; set; }
    public byte DeviceFunction { get; set; }
    public byte DeviceClass { get; set; }
    public byte SystemInstance { get; set; }
    public byte IndustryGroup { get; set; } = 4;
    public bool ArbitraryAddressCapable { get; set; } = true;
}

public sealed class ProductInformationConfig
{
    public ushort Nmea2000Version { get; set; } = 2100;
    public ushort ProductCode { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string SoftwareVersionCode { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string ModelSerialCode { get; set; } = string.Empty;
    public byte CertificationLevel { get; set; } = 1;
    public byte LoadEquivalency { get; set; } = 1;
}

public sealed class ConfigurationInformationConfig
{
    public string InstallationDescription1 { get; set; } = string.Empty;
    public string InstallationDescription2 { get; set; } = string.Empty;
    public string ManufacturerInformation { get; set; } = string.Empty;
}
