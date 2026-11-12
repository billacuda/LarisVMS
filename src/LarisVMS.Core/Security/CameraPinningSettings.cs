namespace LarisVMS.Core.Security;

/// <summary>Setting for <see cref="CameraCertificatePins"/>.</summary>
public static class CameraPinningSettings
{
    /// <summary>Global bool, off by default: on, cameras reached over HTTPS must keep the certificate
    /// first seen at their address.</summary>
    public const string EnabledKey = "Cameras.CertificatePinning";
}
