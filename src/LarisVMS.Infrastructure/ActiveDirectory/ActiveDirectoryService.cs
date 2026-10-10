using System.DirectoryServices.Protocols;
using System.Net;
using LarisVMS.Core.Entities;

namespace LarisVMS.Infrastructure.ActiveDirectory;

/// <summary>AD couldn't be reached or queried — a DC down, a bad service-account password, an LDAPS
/// certificate the server doesn't trust. Never means "wrong user password".</summary>
public class ActiveDirectoryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The user's own name/password were rejected by AD (LDAP result 49) — also what AD returns
/// for a locked, disabled or expired account.</summary>
public class AdInvalidCredentialsException() : Exception("Invalid username or password.");

/// <summary>Everything one full sync needs: each linked group SID resolved (null = no longer exists in
/// AD) and every member of every resolved group.</summary>
public sealed record AdDirectorySnapshot(
    IReadOnlyDictionary<string, AdGroupEntry?> Groups,
    IReadOnlyList<AdUserEntry> Users);

public interface IActiveDirectoryService
{
    /// <summary>Checks the password by binding as the user, then reads their entry and which of
    /// <paramref name="linkedGroupSids"/> they belong to (nested included, via tokenGroups).</summary>
    Task<AdUserEntry> AuthenticateAsync(ActiveDirectorySettings settings, string samAccountName, string password,
        IReadOnlyCollection<string> linkedGroupSids, CancellationToken ct = default);

    /// <summary>Security groups whose name starts with <paramref name="prefix"/>, alphabetical.</summary>
    Task<IReadOnlyList<AdGroupEntry>> SearchGroupsAsync(ActiveDirectorySettings settings, string prefix, int take,
        CancellationToken ct = default);

    Task<AdGroupEntry?> GetGroupBySidAsync(ActiveDirectorySettings settings, string sid, CancellationToken ct = default);

    /// <summary>Throws on any LDAP failure rather than returning a partial snapshot — sync must never
    /// act on incomplete data.</summary>
    Task<AdDirectorySnapshot> ReadSnapshotAsync(ActiveDirectorySettings settings, IReadOnlyCollection<string> linkedGroupSids,
        CancellationToken ct = default);

    /// <summary>Binds with the lookup identity and reads rootDSE. Returns a one-line description.</summary>
    Task<string> TestConnectionAsync(ActiveDirectorySettings settings, CancellationToken ct = default);
}

/// <summary>
/// System.DirectoryServices.Protocols over LDAPS (636) or LDAP (389). Lookups use either the web
/// service's own identity (integrated — Negotiate with no explicit credential) or a configured service
/// account. Plain LDAP turns on Kerberos signing and sealing so traffic is still protected where the DC
/// supports it, but the UI warns against it regardless.
/// </summary>
public sealed class ActiveDirectoryService : IActiveDirectoryService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private const int PageSize = 500;
    private const int InvalidCredentials = 49;

    private static readonly string[] UserAttributes =
        ["objectSid", "sAMAccountName", "mail", "displayName", "userAccountControl", "accountExpires"];
    private static readonly string[] GroupAttributes = ["cn", "objectSid", "distinguishedName"];

    public Task<AdUserEntry> AuthenticateAsync(ActiveDirectorySettings settings, string samAccountName, string password,
        IReadOnlyCollection<string> linkedGroupSids, CancellationToken ct = default) => Task.Run(() =>
    {
        // An empty password is an *unauthenticated* simple bind, which LDAP servers accept — it must
        // never reach Bind() as a "successful" sign-in.
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(samAccountName))
            throw new AdInvalidCredentialsException();

        var domain = RequireDomain(settings);
        try
        {
            using var userConnection = Connect(settings, new NetworkCredential(samAccountName, password, domain));
        }
        catch (LdapException ex) when (ex.ErrorCode == InvalidCredentials)
        {
            throw new AdInvalidCredentialsException();
        }
        catch (LdapException ex)
        {
            throw Wrap(ex, settings);
        }

        using var connection = ConnectForLookup(settings);
        var baseDn = ReadBaseDn(connection, domain);

        var filter = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={LdapFilter.Escape(samAccountName)}))";
        var found = Send(connection, new SearchRequest(baseDn, filter, SearchScope.Subtree, UserAttributes));
        if (found.Entries.Count == 0) throw new AdInvalidCredentialsException();
        var entry = found.Entries[0];

        var tokenGroups = Send(connection, new SearchRequest(entry.DistinguishedName, "(objectClass=*)", SearchScope.Base, "tokenGroups"));
        var memberOf = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tokenGroups.Entries.Count > 0 && tokenGroups.Entries[0].Attributes["tokenGroups"] is { } attr)
        {
            foreach (var value in attr.GetValues(typeof(byte[])).Cast<byte[]>())
            {
                var sid = AdSid.ToSddl(value);
                if (linkedGroupSids.Contains(sid, StringComparer.OrdinalIgnoreCase)) memberOf.Add(sid);
            }
        }

        return ToUserEntry(entry, memberOf);
    }, ct);

    public Task<IReadOnlyList<AdGroupEntry>> SearchGroupsAsync(ActiveDirectorySettings settings, string prefix, int take,
        CancellationToken ct = default) => Task.Run<IReadOnlyList<AdGroupEntry>>(() =>
    {
        prefix = prefix.Trim();
        if (prefix.Length == 0 || take <= 0) return [];

        using var connection = ConnectForLookup(settings);
        var baseDn = ReadBaseDn(connection, RequireDomain(settings));
        var escaped = LdapFilter.Escape(prefix);
        // Security groups only (groupType bit 0x80000000) — tokenGroups, which sign-in uses for
        // membership, only lists security groups, so a distribution group could never grant a role.
        var filter = $"(&(objectCategory=group)(groupType:1.2.840.113556.1.4.803:=2147483648)(|(cn={escaped}*)(sAMAccountName={escaped}*)))";
        var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, GroupAttributes) { SizeLimit = take };
        request.Controls.Add(new SortRequestControl(new SortKey("cn", null, false)) { IsCritical = false });

        SearchResponse response;
        try
        {
            response = (SearchResponse)connection.SendRequest(request, RequestTimeout);
        }
        catch (DirectoryOperationException ex) when (ex.Response is SearchResponse partial && partial.ResultCode == ResultCode.SizeLimitExceeded)
        {
            response = partial;
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
        {
            throw Wrap(ex, settings);
        }

        return response.Entries.Cast<SearchResultEntry>()
            .Select(ToGroupEntry)
            .OfType<AdGroupEntry>()
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToList();
    }, ct);

    public Task<AdGroupEntry?> GetGroupBySidAsync(ActiveDirectorySettings settings, string sid, CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = ConnectForLookup(settings);
        return LookupGroup(connection, settings, sid);
    }, ct);

    public Task<AdDirectorySnapshot> ReadSnapshotAsync(ActiveDirectorySettings settings, IReadOnlyCollection<string> linkedGroupSids,
        CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = ConnectForLookup(settings);
        var baseDn = ReadBaseDn(connection, RequireDomain(settings));

        var groups = new Dictionary<string, AdGroupEntry?>(StringComparer.OrdinalIgnoreCase);
        foreach (var sid in linkedGroupSids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            groups[sid] = LookupGroup(connection, settings, sid);
        }

        var users = new Dictionary<string, (SearchResultEntry Entry, HashSet<string> Groups)>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups.Values.OfType<AdGroupEntry>())
        {
            ct.ThrowIfCancellationRequested();
            // Transitive membership (LDAP_MATCHING_RULE_IN_CHAIN) covers nested groups. A user's
            // primary group (usually Domain Users) isn't in its member attribute at all, so it's
            // matched separately by primaryGroupID.
            var rid = AdSid.Rid(group.Sid);
            var filter = $"(&(objectCategory=person)(objectClass=user)(|(memberOf:1.2.840.113556.1.4.1941:={LdapFilter.Escape(group.DistinguishedName)})(primaryGroupID={rid})))";
            foreach (var entry in SearchPaged(connection, settings, baseDn, filter, UserAttributes, ct))
            {
                var userSid = ReadSid(entry);
                if (userSid is null) continue;
                if (!users.TryGetValue(userSid, out var existing))
                {
                    existing = (entry, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    users[userSid] = existing;
                }
                existing.Groups.Add(group.Sid);
            }
        }

        var userEntries = users.Values.Select(u => ToUserEntry(u.Entry, u.Groups)).ToList();
        return new AdDirectorySnapshot(groups, userEntries);
    }, ct);

    public Task<string> TestConnectionAsync(ActiveDirectorySettings settings, CancellationToken ct = default) => Task.Run(() =>
    {
        using var connection = ConnectForLookup(settings);
        var rootDse = Send(connection, new SearchRequest(null, "(objectClass=*)", SearchScope.Base, "defaultNamingContext", "dnsHostName"));
        var root = rootDse.Entries.Count > 0 ? rootDse.Entries[0] : null;
        var host = ReadString(root, "dnsHostName") ?? settings.Domain;
        var naming = ReadString(root, "defaultNamingContext") ?? AdDomain.ToBaseDn(RequireDomain(settings));

        // Confirms the lookup identity can actually read the directory, not just bind.
        Send(connection, new SearchRequest(naming, "(objectClass=*)", SearchScope.Base, "distinguishedName"));

        var how = settings.UseServiceAccount ? $"as {settings.ServiceAccountUsername}" : "with integrated security";
        var transport = settings.UseLdaps ? "LDAPS (636)" : "LDAP (389)";
        return $"Connected to {host} over {transport} {how}.";
    }, ct);

    // ── Internals ────────────────────────────────────────────────────────────

    private static string RequireDomain(ActiveDirectorySettings settings) =>
        AdDomain.IsValid(settings.Domain)
            ? settings.Domain!.Trim()
            : throw new ActiveDirectoryException("No valid Active Directory domain is configured.");

    private static LdapConnection ConnectForLookup(ActiveDirectorySettings settings)
    {
        NetworkCredential? credential = null;
        if (settings.UseServiceAccount)
        {
            if (string.IsNullOrWhiteSpace(settings.ServiceAccountUsername) || string.IsNullOrEmpty(settings.ServiceAccountPassword))
                throw new ActiveDirectoryException("The service account username or password isn't set.");
            var (domain, user) = AdUsername.SplitServiceAccount(settings.ServiceAccountUsername, RequireDomain(settings));
            credential = new NetworkCredential(user, settings.ServiceAccountPassword, domain);
        }

        try
        {
            return Connect(settings, credential);
        }
        catch (LdapException ex) when (ex.ErrorCode == InvalidCredentials)
        {
            throw new ActiveDirectoryException(settings.UseServiceAccount
                ? "Active Directory rejected the service account's username or password."
                : "Active Directory rejected this server's own identity. Use a service account, or run the web service as a domain account.", ex);
        }
        catch (LdapException ex)
        {
            throw Wrap(ex, settings);
        }
    }

    /// <summary>Opens and binds. A null credential means the process's own identity.</summary>
    private static LdapConnection Connect(ActiveDirectorySettings settings, NetworkCredential? credential)
    {
        var identifier = new LdapDirectoryIdentifier(RequireDomain(settings), settings.Port);
        var connection = new LdapConnection(identifier, credential, AuthType.Negotiate) { Timeout = RequestTimeout };
        try
        {
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
            if (settings.UseLdaps)
            {
                connection.SessionOptions.SecureSocketLayer = true;
            }
            else
            {
                connection.SessionOptions.Signing = true;
                connection.SessionOptions.Sealing = true;
            }
            connection.Bind();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static string ReadBaseDn(LdapConnection connection, string domain)
    {
        try
        {
            var response = (SearchResponse)connection.SendRequest(
                new SearchRequest(null, "(objectClass=*)", SearchScope.Base, "defaultNamingContext"), RequestTimeout);
            var value = ReadString(response.Entries.Count > 0 ? response.Entries[0] : null, "defaultNamingContext");
            if (!string.IsNullOrEmpty(value)) return value;
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
        {
            // Fall through to the DNS-derived DN — same value on any normal AD domain.
        }
        return AdDomain.ToBaseDn(domain);
    }

    private static AdGroupEntry? LookupGroup(LdapConnection connection, ActiveDirectorySettings settings, string sid)
    {
        try
        {
            var response = (SearchResponse)connection.SendRequest(
                new SearchRequest($"<SID={sid}>", "(objectClass=group)", SearchScope.Base, GroupAttributes), RequestTimeout);
            return response.Entries.Count == 0 ? null : ToGroupEntry(response.Entries[0]);
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return null;
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
        {
            throw Wrap(ex, settings);
        }
    }

    private static IEnumerable<SearchResultEntry> SearchPaged(LdapConnection connection, ActiveDirectorySettings settings,
        string baseDn, string filter, string[] attributes, CancellationToken ct)
    {
        var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, attributes);
        var page = new PageResultRequestControl(PageSize);
        request.Controls.Add(page);

        var results = new List<SearchResultEntry>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            SearchResponse response;
            try
            {
                response = (SearchResponse)connection.SendRequest(request, RequestTimeout);
            }
            catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
            {
                throw Wrap(ex, settings);
            }

            results.AddRange(response.Entries.Cast<SearchResultEntry>());
            var cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
            if (cookie is null || cookie.Length == 0) break;
            page.Cookie = cookie;
        }
        return results;
    }

    private static SearchResponse Send(LdapConnection connection, SearchRequest request)
    {
        try
        {
            return (SearchResponse)connection.SendRequest(request, RequestTimeout);
        }
        catch (LdapException ex)
        {
            throw new ActiveDirectoryException(ex.Message, ex);
        }
        catch (DirectoryOperationException ex)
        {
            throw new ActiveDirectoryException(ex.Message, ex);
        }
    }

    private static ActiveDirectoryException Wrap(Exception ex, ActiveDirectorySettings settings)
    {
        if (ex is LdapException { ErrorCode: 81 or 91 })
        {
            var hint = settings.UseLdaps
                ? " If the domain controller is up, check that it has an LDAPS certificate this server trusts."
                : "";
            return new ActiveDirectoryException(
                $"Can't reach a domain controller for {settings.Domain} on port {settings.Port}.{hint}", ex);
        }
        return new ActiveDirectoryException($"Active Directory error: {ex.Message}", ex);
    }

    private static AdUserEntry ToUserEntry(SearchResultEntry entry, IEnumerable<string> groupSids)
    {
        var uac = int.TryParse(ReadString(entry, "userAccountControl"), out var u) ? u : 0;
        var expires = long.TryParse(ReadString(entry, "accountExpires"), out var e) ? e : 0;
        return new AdUserEntry(
            ReadSid(entry) ?? throw new ActiveDirectoryException("A directory user has no objectSid."),
            ReadString(entry, "sAMAccountName") ?? "",
            ReadString(entry, "mail"),
            ReadString(entry, "displayName"),
            AdAccount.IsActive(uac, expires, DateTime.UtcNow),
            groupSids.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static AdGroupEntry? ToGroupEntry(SearchResultEntry entry)
    {
        var sid = ReadSid(entry);
        if (sid is null) return null;
        var name = ReadString(entry, "cn") ?? entry.DistinguishedName;
        return new AdGroupEntry(sid, name, entry.DistinguishedName);
    }

    private static string? ReadSid(SearchResultEntry entry)
    {
        var attr = entry.Attributes["objectSid"];
        if (attr is null || attr.Count == 0) return null;
        return attr.GetValues(typeof(byte[])).FirstOrDefault() is byte[] bytes ? AdSid.ToSddl(bytes) : null;
    }

    private static string? ReadString(SearchResultEntry? entry, string name)
    {
        var attr = entry?.Attributes[name];
        if (attr is null || attr.Count == 0) return null;
        return attr.GetValues(typeof(string)).FirstOrDefault() as string;
    }
}
