using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using LarisVMS.Core.Entities;

namespace LarisVMS.Web.Auth;

/// <summary>
/// Authenticator-app codes (TOTP: 30-second steps, 6 digits, HMAC-SHA1, as every authenticator app
/// makes them) that work once: the step a code was accepted for is remembered with the account, and
/// a code for that step or an earlier one is refused, so a code someone saw being typed can't be used
/// again in the same half-minute. Accepts the same window as ASP.NET Identity's own provider (two
/// steps either side, for phones whose clock is a little off).
/// </summary>
public sealed class SingleUseAuthenticatorTokenProvider : AuthenticatorTokenProvider<ApplicationUser>
{
    internal const string LoginProvider = "LarisVMS";
    internal const string LastStepName = "LastAuthenticatorStep";
    private const int Window = 2;

    public override async Task<bool> ValidateAsync(string purpose, string token, UserManager<ApplicationUser> manager, ApplicationUser user)
    {
        var key = await manager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key) || !int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var code)) return false;

        var step = CurrentStep(DateTimeOffset.UtcNow);
        var matched = MatchingStep(Base32Decode(key), code, step);
        if (matched is not { } used) return false;

        var last = await manager.GetAuthenticationTokenAsync(user, LoginProvider, LastStepName);
        if (long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var lastUsed) && used <= lastUsed) return false;

        var saved = await manager.SetAuthenticationTokenAsync(user, LoginProvider, LastStepName, used.ToString(CultureInfo.InvariantCulture));
        return saved.Succeeded;
    }

    internal static long CurrentStep(DateTimeOffset now) => now.ToUnixTimeSeconds() / 30;

    /// <summary>The time step within the window that <paramref name="code"/> is for, or null.</summary>
    internal static long? MatchingStep(byte[] key, int code, long step)
    {
        for (var offset = -Window; offset <= Window; offset++)
            if (Code(key, step + offset) == code) return step + offset;
        return null;
    }

    internal static int Code(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, hash);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return binary % 1_000_000;
    }

    /// <summary>RFC 4648 Base32, as Identity stores authenticator keys.</summary>
    internal static byte[] Base32Decode(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('=').ToUpperInvariant())
        {
            var value = alphabet.IndexOf(c);
            if (value < 0) continue;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }
        return bytes.ToArray();
    }
}
