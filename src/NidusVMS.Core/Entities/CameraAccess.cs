using NidusVMS.Core.Enums;

namespace NidusVMS.Core.Entities;

/// <summary>
/// Per-camera ACL layered on top of the global Resource×Action RBAC — an NVR needs "who can see
/// which cameras / export / PTZ / talk", which a global role alone can't express. Cameras don't
/// exist until M2; this table is defined now so M1's permission model is complete, and ScopeId is
/// left as a plain Guid (no FK) until the Camera/CameraGroup entities land.
/// </summary>
public class CameraAccess
{
    public Guid Id { get; set; }
    public CameraAccessPrincipalType PrincipalType { get; set; }

    /// <summary>IdentityRole.Id when PrincipalType is Role, ApplicationUser.Id when User.</summary>
    public string PrincipalId { get; set; } = string.Empty;

    public CameraAccessScopeType ScopeType { get; set; }

    /// <summary>CameraGroup.Id or Camera.Id. Null when ScopeType is All.</summary>
    public Guid? ScopeId { get; set; }

    public CameraAccessActions Actions { get; set; }
}
