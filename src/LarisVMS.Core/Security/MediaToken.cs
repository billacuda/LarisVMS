using System.Security.Cryptography;
using System.Text;

namespace LarisVMS.Core.Security;

/// <summary>
/// Short-lived HMAC-signed tokens for the M5 live-view media path. LarisVMS.Web issues one, signed
/// with the target node's own MediaSigningKey, when it proxies a live view request through to that
/// node; the node validates it locally with no DB round trip. Shared here (not in
/// LarisVMS.Infrastructure) specifically so LarisVMS.Node — which never references Infrastructure — can
/// validate with the exact same code LarisVMS.Web signs with, rather than two independent
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

    /// <summary>M7 playback: authorizes exactly one segment file (by its exact on-disk path) for
    /// one camera, same short-lived-HMAC shape as Issue/TryValidate above but kept as a separate
    /// pair of methods rather than overloading those — the live-view path is already verified
    /// end-to-end in a real browser and this shouldn't risk touching it. filePath is the *last*
    /// field in the payload (not cameraId-then-filePath-then-exp) specifically so Split(':', 4)
    /// below can treat "everything left over" as the path verbatim, colons and all — a Windows
    /// path's drive-letter colon would otherwise collide with the field separator.</summary>
    public static string IssueForSegment(Guid cameraId, string filePath, string signingKeyHex, TimeSpan validFor)
    {
        var exp = DateTimeOffset.UtcNow.Add(validFor).ToUnixTimeSeconds();
        var payload = $"segment:{cameraId:N}:{exp}:{filePath}";
        return $"{payload}.{Sign(payload, signingKeyHex)}";
    }

    public static bool TryValidateSegment(string? token, Guid expectedCameraId, string expectedFilePath, string signingKeyHex, out string error)
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

        var parts = payload.Split(':', 4);
        if (parts.Length != 4 || parts[0] != "segment") { error = "malformed payload"; return false; }
        if (!Guid.TryParse(parts[1], out var cameraId) || !long.TryParse(parts[2], out var exp))
        {
            error = "malformed payload";
            return false;
        }
        var filePath = parts[3];

        if (cameraId != expectedCameraId) { error = "camera mismatch"; return false; }
        if (!string.Equals(filePath, expectedFilePath, StringComparison.Ordinal)) { error = "path mismatch"; return false; }
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) { error = "expired"; return false; }

        return true;
    }

    /// <summary>Export trigger (Web -&gt; Node): authorizes exactly one export item request for one
    /// camera — same short-lived-HMAC shape as IssueForSegment but binds cameraId + exportItemId
    /// instead of cameraId + filePath, since the whole point of this call is *telling* the node
    /// which files to concat; there's nothing to bind ahead of time the way a specific segment
    /// path is. A separate pair from IssueForSegment/IssueForExportDownload below, not an overload
    /// of either, for the same reason those two stayed apart: each authorizes a materially different
    /// thing and a bug in one shouldn't be able to touch the other two.</summary>
    public static string IssueForExport(Guid cameraId, Guid exportItemId, string signingKeyHex, TimeSpan validFor)
    {
        var exp = DateTimeOffset.UtcNow.Add(validFor).ToUnixTimeSeconds();
        var payload = $"export:{cameraId:N}:{exportItemId:N}:{exp}";
        return $"{payload}.{Sign(payload, signingKeyHex)}";
    }

    public static bool TryValidateExport(string? token, Guid expectedCameraId, Guid expectedExportItemId, string signingKeyHex, out string error)
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

        var parts = payload.Split(':', 4);
        if (parts.Length != 4 || parts[0] != "export") { error = "malformed payload"; return false; }
        if (!Guid.TryParse(parts[1], out var cameraId) || !Guid.TryParse(parts[2], out var exportItemId) || !long.TryParse(parts[3], out var exp))
        {
            error = "malformed payload";
            return false;
        }

        if (cameraId != expectedCameraId) { error = "camera mismatch"; return false; }
        if (exportItemId != expectedExportItemId) { error = "export item mismatch"; return false; }
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) { error = "expired"; return false; }

        return true;
    }

    /// <summary>Export download (Node -&gt; browser, proxied through Web): authorizes exactly one
    /// finished export file by its exact on-disk path, same shape/reasoning as
    /// IssueForSegment/TryValidateSegment above — filePath is the last payload field for the same
    /// reason (a Windows drive-letter colon can't collide with the field separator that way).</summary>
    public static string IssueForExportDownload(Guid exportItemId, string filePath, string signingKeyHex, TimeSpan validFor)
    {
        var exp = DateTimeOffset.UtcNow.Add(validFor).ToUnixTimeSeconds();
        var payload = $"exportfile:{exportItemId:N}:{exp}:{filePath}";
        return $"{payload}.{Sign(payload, signingKeyHex)}";
    }

    public static bool TryValidateExportDownload(string? token, Guid expectedExportItemId, string expectedFilePath, string signingKeyHex, out string error)
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

        var parts = payload.Split(':', 4);
        if (parts.Length != 4 || parts[0] != "exportfile") { error = "malformed payload"; return false; }
        if (!Guid.TryParse(parts[1], out var exportItemId) || !long.TryParse(parts[2], out var exp))
        {
            error = "malformed payload";
            return false;
        }
        var filePath = parts[3];

        if (exportItemId != expectedExportItemId) { error = "export item mismatch"; return false; }
        if (!string.Equals(filePath, expectedFilePath, StringComparison.Ordinal)) { error = "path mismatch"; return false; }
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) { error = "expired"; return false; }

        return true;
    }

    private static string Sign(string payload, string signingKeyHex)
        => Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(signingKeyHex), Encoding.UTF8.GetBytes(payload)));
}
