namespace LarisVMS.Core.Entities;

/// <summary>
/// Singleton row for the Home Assistant integration (Settings → Home Assistant): the MQTT broker
/// LarisVMS publishes to, and the role whose camera access and permissions decide what's published and
/// which commands Home Assistant may send. The password is encrypted at rest, like the sign-in
/// secrets. Read by HomeAssistantBridge, which reconnects when it's saved.
/// </summary>
public class HomeAssistantSettings
{
    public const int DefaultPort = 1883;
    public const string DefaultTopicPrefix = "larisvms";
    public const string DefaultDiscoveryPrefix = "homeassistant";

    public Guid Id { get; set; }
    public bool IsEnabled { get; set; }
    public string? BrokerHost { get; set; }
    public int BrokerPort { get; set; } = DefaultPort;
    public bool UseTls { get; set; }
    /// <summary>Accept a broker certificate that isn't trusted (a self-signed one on the LAN).</summary>
    public bool AllowUntrustedCertificate { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string TopicPrefix { get; set; } = DefaultTopicPrefix;
    public string DiscoveryPrefix { get; set; } = DefaultDiscoveryPrefix;
    /// <summary>The role Home Assistant acts as. Nothing is published until one is chosen.</summary>
    public string? RoleId { get; set; }
    public DateTime? LastModifiedAt { get; set; }
    public string? LastModifiedBy { get; set; }
}
