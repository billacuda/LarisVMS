namespace LarisVMS.Core.Enums;

public enum CameraAccessPrincipalType
{
    Role = 0,
    /// <summary>A personal grant. No longer honoured or created: camera access is role-based. Kept
    /// only so the one-time conversion (RoleSeedService.RoleOnlyCameraAccessAsync) can find old rows.</summary>
    User = 1
}

public enum CameraAccessScopeType
{
    All = 0,
    Group = 1,
    Camera = 2
}

[Flags]
public enum CameraAccessActions
{
    None = 0,
    View = 1,
    Playback = 2,
    Export = 4,
    Ptz = 8,
    Talk = 16,
    Configure = 32,
    Record = 64
}
