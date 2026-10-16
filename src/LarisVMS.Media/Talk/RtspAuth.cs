using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LarisVMS.Media.Talk;

/// <summary>RTSP authentication (RFC 2617 Digest, falling back to Basic) for the backchannel client.
/// Built from a 401's WWW-Authenticate header, then asked for an Authorization header per request —
/// a digest response depends on the method and URI, so it can't be computed once.</summary>
public sealed partial class RtspAuth
{
    private readonly string _user;
    private readonly string _password;
    private readonly bool _digest;
    private readonly string _realm = "";
    private readonly string _nonce = "";
    private readonly string? _opaque;
    private readonly string? _qop;
    private int _nonceCount;

    private RtspAuth(string user, string password, bool digest, string realm, string nonce, string? opaque, string? qop)
    {
        _user = user;
        _password = password;
        _digest = digest;
        _realm = realm;
        _nonce = nonce;
        _opaque = opaque;
        _qop = qop;
    }

    /// <summary>Picks Digest when any challenge offers it (cameras often send both), else Basic. Null
    /// when there's no usable challenge.</summary>
    public static RtspAuth? FromChallenges(IEnumerable<string> challenges, string user, string password)
    {
        var list = challenges.ToList();
        var digest = list.FirstOrDefault(c => c.TrimStart().StartsWith("Digest", StringComparison.OrdinalIgnoreCase));
        if (digest is not null)
        {
            var p = ParseParams(digest.TrimStart()[6..]);
            // Only MD5 (the RTSP norm). "auth" is the only qop worth supporting; auth-int needs the body.
            var qop = p.TryGetValue("qop", out var q) && q.Split(',').Any(x => x.Trim() == "auth") ? "auth" : null;
            return new RtspAuth(user, password, true, p.GetValueOrDefault("realm", ""), p.GetValueOrDefault("nonce", ""),
                p.GetValueOrDefault("opaque"), qop);
        }
        if (list.Any(c => c.TrimStart().StartsWith("Basic", StringComparison.OrdinalIgnoreCase)))
            return new RtspAuth(user, password, false, "", "", null, null);
        return null;
    }

    public string Header(string method, string uri, string? cnonce = null)
    {
        if (!_digest)
            return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_user}:{_password}"));

        var ha1 = Md5($"{_user}:{_realm}:{_password}");
        var ha2 = Md5($"{method}:{uri}");
        var sb = new StringBuilder($"Digest username=\"{_user}\", realm=\"{_realm}\", nonce=\"{_nonce}\", uri=\"{uri}\"");
        if (_qop is not null)
        {
            var nc = (++_nonceCount).ToString("x8");
            cnonce ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            sb.Append($", response=\"{Md5($"{ha1}:{_nonce}:{nc}:{cnonce}:{_qop}:{ha2}")}\", qop={_qop}, nc={nc}, cnonce=\"{cnonce}\"");
        }
        else
        {
            sb.Append($", response=\"{Md5($"{ha1}:{_nonce}:{ha2}")}\"");
        }
        if (_opaque is not null) sb.Append($", opaque=\"{_opaque}\"");
        return sb.ToString();
    }

    internal static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static Dictionary<string, string> ParseParams(string s)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ParamRegex().Matches(s))
            result[m.Groups["k"].Value] = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["v"].Value;
        return result;
    }

    [GeneratedRegex("""(?<k>[A-Za-z0-9_-]+)\s*=\s*(?:"(?<q>[^"]*)"|(?<v>[^\s,]+))""")]
    private static partial Regex ParamRegex();
}
