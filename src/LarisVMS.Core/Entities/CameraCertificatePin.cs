namespace LarisVMS.Core.Entities;

/// <summary>A camera address's remembered HTTPS certificate (see <c>CameraCertificatePins</c>).</summary>
public class CameraCertificatePin
{
    /// <summary>Host name or IP address, lower case, no port.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>SHA-256 of the certificate, upper-case hex.</summary>
    public string Thumbprint { get; set; } = string.Empty;

    public DateTime FirstSeenUtc { get; set; }
}
