using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FakeOnvif;

/// <summary>cameras.json, shared with the PowerShell scripts.</summary>
public sealed class DemoConfig
{
    public string DataRoot { get; init; } = "";
    public NetworkConfig Network { get; init; } = new();
    public int RtspPort { get; init; } = 554;
    public List<CameraConfig> Cameras { get; init; } = [];

    public string ClipsDir => Path.Combine(DataRoot, "clips");

    public CameraConfig? FindByIp(string ip) => Cameras.FirstOrDefault(c => c.Ip == ip);

    public static DemoConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<DemoConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Could not read {path}.");
    }
}

public sealed class NetworkConfig
{
    public string SwitchName { get; init; } = "LarisDemo";
    public string HostIp { get; init; } = "10.77.0.1";
    public int PrefixLength { get; init; } = 24;
}

public sealed class CameraConfig
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Ip { get; init; } = "";
    public string Model { get; init; } = "";
    public string Codec { get; init; } = "H264";
    public bool Audio { get; init; }

    public const string Manufacturer = "LarisDemo";
    public string Firmware => "V2.4.1 build 260915";
    public string HardwareId => $"{Model}-HW1";

    /// <summary>Stable per camera, so re-adding a camera after a restart matches the same device.</summary>
    public Guid Uuid => new(MD5.HashData(Encoding.UTF8.GetBytes("larisvms-demo-" + Id)));
    public string SerialNumber => "LD" + Convert.ToHexString(Uuid.ToByteArray())[..10];

    public string DeviceServiceUrl => $"http://{Ip}/onvif/device_service";
    public string MediaServiceUrl => $"http://{Ip}/onvif/media_service";
    public string EventServiceUrl => $"http://{Ip}/onvif/event_service";
    public string SnapshotUrl => $"http://{Ip}/snapshot.jpg";
}
