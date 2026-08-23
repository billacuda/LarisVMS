namespace LarisVMS.Core.Entities;

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

    /// <summary>Every camera belonging to this group — many-to-many (see Camera.Groups' own doc
    /// comment for the single-site invariant CameraGroupPolicy enforces on top of this).</summary>
    public ICollection<Camera> Cameras { get; set; } = [];

    /// <summary>A top-level node is a "Site" in the roles/permissions overhaul's org &gt; site &gt;
    /// camera_group scope hierarchy; anything underneath one is a "Camera Group". Not EF-mapped —
    /// use <c>g.ParentId == null</c> directly in server-side LINQ queries (translates to SQL), this
    /// property only for already-materialized C# code (Razor views, in-memory LINQ).</summary>
    public bool IsSite => ParentId is null;
}
