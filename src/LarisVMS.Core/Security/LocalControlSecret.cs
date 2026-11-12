using System.Security.Cryptography;
using System.Text;

namespace LarisVMS.Core.Security;

/// <summary>
/// The secret the node and its Vision Service share for their loopback control channel. Listening on
/// 127.0.0.1 keeps the network out but not other people signed in to the same machine; with this,
/// a request needs the secret the node made at start and handed to the Vision process it launched
/// (in its environment, which other non-admin users can't read). Each side checks it on every
/// request from the other.
/// </summary>
public static class LocalControlSecret
{
    public const string HeaderName = "X-LarisVMS-Local-Key";

    /// <summary>Configuration key in the Vision Service (environment: Vision__ControlSecret).</summary>
    public const string VisionConfigKey = "Vision:ControlSecret";
    public const string VisionEnvironmentVariable = "Vision__ControlSecret";

    public static string New() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Whether <paramref name="presented"/> is the secret. An empty expected secret matches nothing.</summary>
    public static bool Matches(string? expected, string? presented) =>
        !string.IsNullOrEmpty(expected) && presented is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));
}
