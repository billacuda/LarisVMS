namespace Rcordr.Core.Entities;

/// <summary>
/// Resource×Action RBAC row: "does RoleId have Action on Resource". Resolved on the fly by
/// PermissionPolicyProvider into an authorization policy named "{Resource}.{Action}"
/// (e.g. [Authorize("Cameras.Edit")]) with no per-combination AddPolicy registration required.
/// </summary>
public class Permission
{
    public Guid Id { get; set; }
    public string RoleId { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public bool IsSystemPermission { get; set; }
}
