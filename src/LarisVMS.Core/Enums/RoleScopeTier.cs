namespace LarisVMS.Core.Enums;

/// <summary>How wide a role is allowed to be scoped, per the roles/permissions overhaul's
/// organization &gt; site &gt; camera_group hierarchy. "Org" has no CameraGroup analogue — it means
/// unrestricted/global, the same meaning <c>null</c> already carries as
/// ICameraAccessService.GetAccessibleCameraIdsAsync's return value. "Site" is a top-level
/// CameraGroup (ParentId == null); "Group" is any CameraGroup underneath one.</summary>
public enum RoleScopeTier
{
    Org = 0,
    Site = 1,
    Group = 2
}
