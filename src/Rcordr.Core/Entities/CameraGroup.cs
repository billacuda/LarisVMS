namespace Rcordr.Core.Entities;

/// <summary>Self-referencing tree for sites/buildings/floors. MaterializedPath (e.g. "/site-a/bldg-2/")
/// is maintained by CameraGroupService on write so ancestor lookups for ISettingsResolver's group-walk
/// don't need a recursive query.</summary>
public class CameraGroup
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MaterializedPath { get; set; } = string.Empty;

    public CameraGroup? Parent { get; set; }
    public ICollection<CameraGroup> Children { get; set; } = [];
    public ICollection<Camera> Cameras { get; set; } = [];
}
