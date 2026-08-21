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

    /// <summary>Hover thumbnail (Web -&gt; Node): authorizes exactly one JPEG frame extraction — one
    /// camera, one exact segment file path, one bucketed offset-in-seconds into that file. Same
    /// shape as IssueForSegment, with offsetSeconds inserted as a new scalar field between cameraId
    /// and exp (filePath stays the *last* field for the same drive-letter-colon-safety reason).
    /// Binding offsetSeconds isn't only a security measure — it forces every request for "this
    /// bucket" to carry the exact same value the node names its cache file after, which is what
    /// makes the on-disk cache actually get reused instead of being defeated by a client sending
    /// slightly different timestamps for what's meant to be the same bucket.</summary>
    public static string IssueForThumbnail(Guid cameraId, string filePath, int offsetSeconds, string signingKeyHex, TimeSpan validFor)
    {
        var exp = DateTimeOffset.UtcNow.Add(validFor).ToUnixTimeSeconds();
        var payload = $"thumb:{cameraId:N}:{offsetSeconds}:{exp}:{filePath}";
        return $"{payload}.{Sign(payload, signingKeyHex)}";
    }

    public static bool TryValidateThumbnail(string? token, Guid expectedCameraId, string expectedFilePath, int expectedOffsetSeconds, string signingKeyHex, out string error)
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

        var parts = payload.Split(':', 5);
        if (parts.Length != 5 || parts[0] != "thumb") { error = "malformed payload"; return false; }
        if (!Guid.TryParse(parts[1], out var cameraId) || !int.TryParse(parts[2], out var offsetSeconds) || !long.TryParse(parts[3], out var exp))
        {
            error = "malformed payload";
            return false;
        }
        var filePath = parts[4];

        if (cameraId != expectedCameraId) { error = "camera mismatch"; return false; }
        if (offsetSeconds != expectedOffsetSeconds) { error = "offset mismatch"; return false; }
        if (!string.Equals(filePath, expectedFilePath, StringComparison.Ordinal)) { error = "path mismatch"; return false; }
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) { error = "expired"; return false; }

        return true;
    }

    /// <summary>Node control (Web -&gt; Node): authorizes one administrative action against the node
    /// itself rather than against any camera's media — currently only "restart", from the Nodes
    /// page's own button. Binds the action name so a token minted for one control operation can
    /// never be replayed against a different one that gets added later, and carries no cameraId at
    /// all because the target is the node process.
    ///
    /// Deliberately its own pair rather than an overload of the media ones: this authorizes stopping
    /// a recorder, which is a materially different (and more consequential) thing than reading a
    /// frame, and the existing media paths are verified end-to-end in a real browser — a bug
    /// introduced by overloading them would be far worse than the small duplication here. Same
    /// reasoning IssueForExport records for staying separate.</summary>
    public static string IssueForNodeControl(string action, string signingKeyHex, TimeSpan validFor)
    {
        var exp = DateTimeOffset.UtcNow.Add(validFor).ToUnixTimeSeconds();
        var payload = $"nodectl:{action}:{exp}";
        return $"{payload}.{Sign(payload, signingKeyHex)}";
    }

    public static bool TryValidateNodeControl(string? token, string expectedAction, string signingKeyHex, out string error)
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

        var parts = payload.Split(':', 3);
        if (parts.Length != 3 || parts[0] != "nodectl") { error = "malformed payload"; return false; }
        if (!long.TryParse(parts[2], out var exp)) { error = "malformed payload"; return false; }

        if (!string.Equals(parts[1], expectedAction, StringComparison.Ordinal)) { error = "action mismatch"; return false; }
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

    /// <summary>Export delete (Web -&gt; Node): authorizes removing exactly one finished export file by
    /// its exact on-disk path. A separate token family from IssueForExportDownload, not a reuse of
    /// it, even though the payload shape is identical — a download token leaking into a log or a
    /// browser history entry must not also be a working delete token for the same file.</summary>
    public static string IssueForExportDelete(Guid exportItemId, string filePath, string signingKeyHex, TimeSpan validFor)
    {
        var exp = DateTimeOffset.UtcNow.Add(validFor).ToUnixTimeSeconds();
        var payload = $"exportdelete:{exportItemId:N}:{exp}:{filePath}";
        return $"{payload}.{Sign(payload, signingKeyHex)}";
    }

    public static bool TryValidateExportDelete(string? token, Guid expectedExportItemId, string expectedFilePath, string signingKeyHex, out string error)
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
        if (parts.Length != 4 || parts[0] != "exportdelete") { error = "malformed payload"; return false; }
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
