namespace Rcordr.Core.Enums;

public enum CameraAccessPrincipalType
{
    Role = 0,
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
    Configure = 32
}
