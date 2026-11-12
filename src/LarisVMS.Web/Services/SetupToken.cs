using System.Net;
using System.Security.Cryptography;
using System.Text;
using LarisVMS.Core.Security;

namespace LarisVMS.Web.Services;

/// <summary>
/// Until the setup wizard has made the first admin account, anyone who could reach the site could
/// otherwise make it theirs. The wizard first asks for a one-time token the server writes to
/// <see cref="FilePath"/> (in the protected data folder: Administrators, SYSTEM, and the service), so
/// only someone who can sign in to the server can set it up. A browser on the server itself, or on
/// the local network, doesn't need it, so a home install just works; <see cref="AlwaysRequireKey"/>
/// asks every other computer for it. The file is deleted once the admin account exists.
/// </summary>
public sealed class SetupToken(ILogger<SetupToken> logger, IConfiguration configuration)
{
    public const string CookieName = ".LarisVMS.Setup";

    /// <summary>appsettings key: true asks for the token from every computer but the server itself,
    /// local network included.</summary>
    public const string AlwaysRequireKey = "Setup:AlwaysRequireToken";

    private readonly Lock _lock = new();
    private string? _token;

    public static string FilePath => Path.Combine(LocalDataAcl.Root, "setup-token.txt");

    /// <summary>Makes the token (or reads the one an earlier start made) and the file holding it.</summary>
    public string Ensure()
    {
        lock (_lock)
        {
            if (_token is not null) return _token;
            try
            {
                if (File.Exists(FilePath) && Normalize(File.ReadAllText(FilePath)) is { Length: 20 } existing)
                    return _token = Format(existing);
                Directory.CreateDirectory(LocalDataAcl.Root);
                _token = Generate();
                File.WriteAllText(FilePath, _token + Environment.NewLine);
                LocalDataAcl.ProtectFile(FilePath);
                logger.LogWarning("Setup needs the token in {Path} when opened from another computer.", FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Can't write it: the wizard is then usable only from the server itself.
                _token ??= Generate();
                logger.LogError(ex, "Could not write the setup token to {Path}; open the setup wizard on the server itself.", FilePath);
            }
            return _token;
        }
    }

    public bool Matches(string? typed)
    {
        var expected = Encoding.ASCII.GetBytes(Normalize(Ensure()));
        var given = Encoding.ASCII.GetBytes(Normalize(typed ?? ""));
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    /// <summary>What the browser keeps once the token was right: derived from it, so it can't be made
    /// without knowing it.</summary>
    public string CookieValue() =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.ASCII.GetBytes(Normalize(Ensure())), "larisvms-setup"u8));

    public bool IsAuthorized(HttpContext context) =>
        IsTrustedRequest(context.Connection.RemoteIpAddress, CameThroughProxy(context.Request.Headers),
            configuration.GetValue<bool>(AlwaysRequireKey))
        || (context.Request.Cookies[CookieName] is { } cookie
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(cookie), Encoding.ASCII.GetBytes(CookieValue())));

    /// <summary>Setup is done: the token is no use any more.</summary>
    public void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete {Path}.", FilePath);
        }
    }

    /// <summary>Headers a proxy adds about the browser behind it. Any of them means the connection's
    /// address is the proxy's, not the browser's, so it doesn't count as local. (A plain TCP
    /// forwarder adds nothing; AlwaysRequireToken covers that.)</summary>
    private static readonly string[] ProxyHeaders =
        // X-Original-For: what ASP.NET leaves after taking the address from a listed proxy's header.
        ["X-Forwarded-For", "X-Original-For", "Forwarded", "X-Real-IP", "Via", "X-Forwarded-Host", "X-Forwarded-Proto",
         "CF-Connecting-IP", "True-Client-IP"];

    internal static bool CameThroughProxy(IHeaderDictionary headers) => ProxyHeaders.Any(headers.ContainsKey);

    /// <summary>A browser on the server itself. Not one whose request came through a proxy on it,
    /// which would look local too.</summary>
    internal static bool IsLocalRequest(IPAddress? remote, bool forwarded) =>
        !forwarded && remote is not null && IPAddress.IsLoopback(remote);

    /// <summary>A browser that can set up a new install without the token: on the server itself, or
    /// (unless <paramref name="alwaysRequire"/>) on the local network. Never one whose request came
    /// through a proxy, whose own address is unknown.</summary>
    internal static bool IsTrustedRequest(IPAddress? remote, bool forwarded, bool alwaysRequire) =>
        IsLocalRequest(remote, forwarded) || (!alwaysRequire && !forwarded && remote is not null && IsPrivate(remote));

    /// <summary>Private and link-local addresses: 10/8, 172.16/12, 192.168/16, 169.254/16, fc00::/7,
    /// and fe80::/10, including IPv4 written as IPv6.</summary>
    internal static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
        }
        return false;
    }

    internal static string Normalize(string value) =>
        new(value.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    // 20 characters from 31 that can't be mistaken for each other (no I, L, O, 0, or 1): about 99 bits.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private static string Generate() =>
        Format(new string(Enumerable.Range(0, 20).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray()));

    private static string Format(string normalized) =>
        string.Join('-', Enumerable.Range(0, 4).Select(i => normalized.Substring(i * 5, 5)));
}
