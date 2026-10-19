using System.Security.Claims;

namespace LarisVMS.Web.Auth;

/// <summary>
/// Entra ID sign-in only reaches accounts an admin linked: on Settings → Users the admin enters the
/// person's Entra object ID, stored as a pending login (<see cref="PendingKey"/>); the first sign-in by
/// that Entra user completes the link. Email addresses aren't used to match — Entra doesn't guarantee
/// they're verified, so matching by email let anyone who could set that address take the account.
/// </summary>
public static class EntraLinks
{
    public const string ObjectIdClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    public static string PendingKey(Guid objectId) => $"pending-oid:{objectId:D}";

    /// <summary>The Entra object ID in a sign-in's claims, if any.</summary>
    public static Guid? ObjectIdOf(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ObjectIdClaim) ?? principal.FindFirstValue("oid"), out var oid) ? oid : null;

    public static bool IsPending(string providerKey) => providerKey.StartsWith("pending-oid:", StringComparison.Ordinal);

    public const string NotLinkedMessage =
        "This Microsoft account isn't linked to an account here. Ask your administrator to link it.";
}
