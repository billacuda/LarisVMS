namespace LarisVMS.Core;

/// <summary>
/// Reads a release tag or version string such as "v0.213.1-beta", "0.214.0" or
/// "0.214.0+f19db159" as a plain <see cref="Version"/>: a leading "v" and anything from the first
/// "-" (pre-release label) or "+" (build metadata) are dropped. GitHub release tags carry both a
/// "v" and a "-beta" suffix, which <see cref="Version.TryParse(string?, out Version?)"/> rejects.
/// </summary>
public static class ReleaseVersion
{
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;

        var s = value.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        var cut = s.IndexOfAny(['-', '+']);
        if (cut >= 0) s = s[..cut];

        if (!Version.TryParse(s, out var parsed)) return false;
        version = parsed;
        return true;
    }
}
