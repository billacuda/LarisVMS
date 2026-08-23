namespace LarisVMS.Core.Auth;

/// <summary>
/// Stable identifiers for the 8 built-in roles the roles/permissions overhaul seeds (see
/// RoleSeedService), stored on RoleProfile.Tag. Referenced from both LarisVMS.Infrastructure
/// (PermissionService/CameraAccessService's matrix-bypass check) and LarisVMS.Web
/// (RoleManagementPolicy's protected-role check) — living in Core keeps both able to see it without
/// Infrastructure depending on Web.
/// </summary>
public static class RoleTags
{
    public const string SuperAdmin = "SUPER";
    public const string SystemAdmin = "ADMIN";
    public const string SecurityManager = "SEC-MGR";
    public const string Operator = "OPERATOR";
    public const string Auditor = "AUDIT";
    public const string Technician = "TECH";
    public const string GuestViewer = "VIEWER";
    public const string ApiIntegration = "API";
}
