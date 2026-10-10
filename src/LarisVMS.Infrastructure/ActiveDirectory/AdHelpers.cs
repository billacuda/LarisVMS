using System.Text;
using System.Text.RegularExpressions;

namespace LarisVMS.Infrastructure.ActiveDirectory;

/// <summary>RFC 4515 escaping for values placed inside an LDAP search filter.</summary>
public static class LdapFilter
{
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\5c"); break;
                case '*': sb.Append(@"\2a"); break;
                case '(': sb.Append(@"\28"); break;
                case ')': sb.Append(@"\29"); break;
                case '\0': sb.Append(@"\00"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}

/// <summary>Binary objectSid → "S-1-5-21-..." without System.Security.Principal.Windows'
/// SecurityIdentifier (Windows-only API surface).</summary>
public static class AdSid
{
    public static string ToSddl(byte[] sid)
    {
        if (sid.Length < 8) throw new ArgumentException("Not a valid SID.", nameof(sid));
        int revision = sid[0];
        int subAuthorityCount = sid[1];
        if (sid.Length < 8 + subAuthorityCount * 4) throw new ArgumentException("Not a valid SID.", nameof(sid));

        long authority = 0;
        for (var i = 2; i <= 7; i++) authority = (authority << 8) | sid[i];

        var sb = new StringBuilder($"S-{revision}-{authority}");
        for (var i = 0; i < subAuthorityCount; i++)
            sb.Append('-').Append(BitConverter.ToUInt32(sid, 8 + i * 4));
        return sb.ToString();
    }

    /// <summary>The relative identifier — the last sub-authority. Matches a user's primaryGroupID
    /// when this is that user's primary group.</summary>
    public static string? Rid(string sddl)
    {
        var dash = sddl.LastIndexOf('-');
        return dash < 0 ? null : sddl[(dash + 1)..];
    }

    public static bool LooksValid(string? sddl) =>
        !string.IsNullOrEmpty(sddl) && Regex.IsMatch(sddl, @"^S-1-\d+(-\d+)+$");
}

public static class AdAccount
{
    private const int UfAccountDisable = 0x2;

    /// <summary>Not disabled (userAccountControl bit 0x2) and not past accountExpires. accountExpires
    /// is a FILETIME; 0 and Int64.MaxValue both mean "never".</summary>
    public static bool IsActive(int userAccountControl, long accountExpires, DateTime utcNow)
    {
        if ((userAccountControl & UfAccountDisable) != 0) return false;
        if (accountExpires is 0 or long.MaxValue) return true;
        try
        {
            return DateTime.FromFileTimeUtc(accountExpires) > utcNow;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}

public static class AdDomain
{
    private static readonly Regex Fqdn = new(
        @"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+$",
        RegexOptions.CultureInvariant);

    public static bool IsValid(string? domain) => !string.IsNullOrWhiteSpace(domain) && Fqdn.IsMatch(domain.Trim());

    /// <summary>"corp.example.com" → "DC=corp,DC=example,DC=com". Fallback for when rootDSE's
    /// defaultNamingContext can't be read; an AD domain's naming context always mirrors its DNS name.</summary>
    public static string ToBaseDn(string domain) =>
        string.Join(",", domain.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => "DC=" + p));

    /// <summary>The NetBIOS-ish first label — used as the credential domain when a service-account
    /// username was entered without a DOMAIN\ prefix.</summary>
    public static string FirstLabel(string domain) => domain.Trim().Split('.')[0];
}

/// <summary>How a sign-in name is routed: anything with an "@" is a local (email) account; anything
/// else is an AD sAMAccountName. A "DOMAIN\" prefix is tolerated and dropped — only one domain is
/// supported, so it carries no information.</summary>
public static class AdUsername
{
    public static bool IsLocal(string input) => input.Contains('@');

    public static string Normalize(string input)
    {
        var trimmed = input.Trim();
        var slash = trimmed.LastIndexOf('\\');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    /// <summary>Splits "DOMAIN\user" for a service-account credential. A UPN ("user@corp.example.com")
    /// is passed through with no domain; a bare name gets the domain's first label.</summary>
    public static (string Domain, string User) SplitServiceAccount(string input, string fallbackDomain)
    {
        var trimmed = input.Trim();
        var slash = trimmed.IndexOf('\\');
        if (slash > 0) return (trimmed[..slash], trimmed[(slash + 1)..]);
        return trimmed.Contains('@') ? ("", trimmed) : (AdDomain.FirstLabel(fallbackDomain), trimmed);
    }
}
