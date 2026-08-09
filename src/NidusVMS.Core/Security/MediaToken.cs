using System.Security.Cryptography;
using System.Text;

namespace NidusVMS.Core.Security;

/// <summary>
/// Short-lived HMAC-signed tokens for the M5 live-view media path. NidusVMS.Web issues one, signed
/// with the target node's own MediaSigningKey, when it proxies a live view request through to that
/// node; the node validates it locally with no DB round trip. Shared here (not in
/// NidusVMS.Infrastructure) specifically so NidusVMS.Node — which never references Infrastructure — can
/// validate with the exact same code NidusVMS.Web signs with, rather than two independent
/// implementations that could quietly drift apart.
/// </summary>
public static class MediaToken
{
    public static string Issue(Guid cameraId, string signingKeyHex, TimeSpan validFor)
    {
        var exp = DateTimeOffset.UtcNow.Add(validFor).ToUnixTimeSeconds();
        var payload = $"{cameraId:N}:{exp}";
        return $"{payload}.{Sign(payload, signingKeyHex)}";
    }

    public static bool TryValidate(string? token, Guid expectedCameraId, string signingKeyHex, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(token)) { error = "missing token"; return false; }

        var dot = token.LastIndexOf('.');
        if (dot < 0) { error = "malformed token"; return false; }
        var payload = token[..dot];
        var providedSig = token[(dot + 1)..];

        byte[] provided, expected;
        try
        {
            provided = Convert.FromHexString(providedSig);
            expected = Convert.FromHexString(Sign(payload, signingKeyHex));
        }
        catch (FormatException)
        {
            error = "malformed signature";
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(provided, expected)) { error = "signature mismatch"; return false; }

        var parts = payload.Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var cameraId) || !long.TryParse(parts[1], out var exp))
        {
            error = "malformed payload";
            return false;
        }

        if (cameraId != expectedCameraId) { error = "camera mismatch"; return false; }
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) { error = "expired"; return false; }

        return true;
    }

    private static string Sign(string payload, string signingKeyHex)
        => Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(signingKeyHex), Encoding.UTF8.GetBytes(payload)));
}
