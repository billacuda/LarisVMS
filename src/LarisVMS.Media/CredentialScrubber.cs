using System.Text.RegularExpressions;

namespace LarisVMS.Media;

/// <summary>
/// Removes embedded credentials — the <c>user:password@</c> in a URL's authority — from text before
/// it is written to a log. Camera RTSP URIs carry credentials inline (see
/// NodeWorker.InjectCredentials), and ffmpeg echoes the full input URL back into its own stderr: in
/// the "Input #0, rtsp, from '…'" banner, and — worse — in the 401/403 "authorization failed" lines
/// this codebase escalates to Warning. Every ffmpeg stderr line, and every "starting ffmpeg for
/// {uri}" line, is routed through <see cref="Scrub"/> first so a camera password never lands in a
/// log file.
/// </summary>
public static class CredentialScrubber
{
    // scheme:// followed by everything up to the LAST '@' before the next '/' or whitespace. The
    // greedy [^\s/]* deliberately swallows a literal '@' inside a not-yet-percent-encoded password
    // ("admin:p@ss@host" → "admin:p@ss" is redacted whole). No '@' in the authority ⇒ no match, so a
    // credential-free URL and ordinary text are left untouched.
    private static readonly Regex UrlUserInfo = new(
        @"([a-zA-Z][a-zA-Z0-9+.\-]*://)[^\s/]*@", RegexOptions.Compiled);

    /// <summary>Returns <paramref name="text"/> with the userinfo of every URL replaced by
    /// <c>***</c>. Safe on arbitrary text — an ffmpeg stderr line, an exception message, a bare
    /// URI. Never throws; null/empty in, empty out.</summary>
    public static string Scrub(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : UrlUserInfo.Replace(text, "$1***@");
}
