namespace LarisVMS.Infrastructure.ActiveDirectory;

/// <summary>One AD user as read from the directory. GroupSids holds the linked groups the user is a
/// (possibly nested) member of; unlinked groups are left out.</summary>
public sealed record AdUserEntry(
    string Sid,
    string SamAccountName,
    string? Email,
    string? DisplayName,
    bool IsActive,
    IReadOnlySet<string> GroupSids);

public sealed record AdGroupEntry(string Sid, string Name, string DistinguishedName);

/// <summary>An existing LarisVMS account already linked to an AD SID.</summary>
public sealed record AdLinkedUserState(
    string UserId,
    string Sid,
    string? UserName,
    string? Email,
    string? DisplayName,
    bool IsEnabled,
    bool DisabledByDirectorySync,
    IReadOnlySet<string> RoleIds);

public sealed record AdLink(string GroupSid, string RoleId);

public enum AdAccountChange
{
    None,
    Create,
    Disable,
    Enable
}

/// <summary>What to do to one account. UserId is null for a Create. Entry is null for a linked
/// account that no longer appears in any linked group (or was deleted from AD).</summary>
public sealed record AdUserAction(
    string Sid,
    string? UserId,
    AdUserEntry? Entry,
    AdAccountChange Change,
    bool IdentityChanged,
    IReadOnlySet<string> TargetRoleIds,
    string? Reason);

/// <summary>
/// Pure decision logic for AD sync, kept free of LDAP and Identity so it can be unit tested. AD owns
/// an AD user's roles entirely: the target role set is always exactly what the linked groups grant.
/// Sync only re-enables accounts it disabled itself — a manual disable in LarisVMS sticks.
/// </summary>
public static class AdSyncPlanner
{
    public const string ReasonAdDisabled = "disabled or expired in Active Directory";
    public const string ReasonNoGroups = "no longer in any linked AD group";

    public static IReadOnlySet<string> RolesFor(AdUserEntry entry, IEnumerable<AdLink> links) =>
        links.Where(l => entry.GroupSids.Contains(l.GroupSid, StringComparer.OrdinalIgnoreCase))
            .Select(l => l.RoleId)
            .ToHashSet(StringComparer.Ordinal);

    /// <param name="fullSync">True for the scheduled sync, where the snapshot is every member of every
    /// linked group — a linked account missing from it has left them all. False for a single user's
    /// sign-in, where the snapshot only holds that user.</param>
    public static List<AdUserAction> Plan(
        IReadOnlyCollection<AdUserEntry> snapshot,
        IReadOnlyCollection<AdLinkedUserState> existing,
        IReadOnlyCollection<AdLink> links,
        bool fullSync)
    {
        var actions = new List<AdUserAction>();
        var existingBySid = existing.ToDictionary(e => e.Sid, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in snapshot)
        {
            if (!seen.Add(entry.Sid)) continue;

            var roles = RolesFor(entry, links);
            var eligible = entry.IsActive && roles.Count > 0;
            var reason = !entry.IsActive ? ReasonAdDisabled : roles.Count == 0 ? ReasonNoGroups : null;

            if (!existingBySid.TryGetValue(entry.Sid, out var user))
            {
                if (eligible)
                    actions.Add(new AdUserAction(entry.Sid, null, entry, AdAccountChange.Create, true, roles, null));
                continue;
            }

            var change = AdAccountChange.None;
            if (!eligible && user.IsEnabled) change = AdAccountChange.Disable;
            else if (eligible && !user.IsEnabled && user.DisabledByDirectorySync) change = AdAccountChange.Enable;

            var identityChanged =
                !string.Equals(user.UserName, entry.SamAccountName, StringComparison.Ordinal) ||
                !string.Equals(user.Email ?? "", entry.Email ?? "", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(user.DisplayName ?? "", entry.DisplayName ?? "", StringComparison.Ordinal);

            actions.Add(new AdUserAction(entry.Sid, user.UserId, entry, change, identityChanged, roles,
                change == AdAccountChange.Disable ? reason : null));
        }

        if (!fullSync) return actions;

        foreach (var user in existing.Where(u => !seen.Contains(u.Sid)))
        {
            actions.Add(new AdUserAction(user.Sid, user.UserId, null,
                user.IsEnabled ? AdAccountChange.Disable : AdAccountChange.None,
                false, new HashSet<string>(), user.IsEnabled ? ReasonNoGroups : null));
        }

        return actions;
    }
}
