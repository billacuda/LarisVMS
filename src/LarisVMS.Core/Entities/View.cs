namespace LarisVMS.Core.Entities;

/// <summary>
/// A saved camera wall layout (M6). LayoutJson holds the GridStack cells — see the plan's "Views &
/// layout" section for the shape: <c>{"cells":[{"id","x","y","w","h","aspect","cameraId",
/// "hideOnPhone"}],"mobileTwoColumn":false}</c>. The mobile arrangement is always derived from this
/// at render time (Pages/Views/Play), never stored — a view built on desktop gets a usable phone
/// layout for free and there is nothing to keep in sync.
///
/// Every view lives in one tree: its owner's Private tree (<see cref="RoleId"/> null) or a role's
/// shared tree, in a <see cref="ViewFolder"/> or at the tree's top level (<see cref="FolderId"/> null).
/// Names are unique within a tree. Who may see and change each tree is decided by ViewService.
/// </summary>
public class View
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>ApplicationUser.Id of whoever created it — and, for a view in a Private tree, the
    /// user whose Private tree it's in (moving a view into someone's Private tree makes it theirs).</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>The role whose shared tree holds this view, or null for the owner's Private tree.</summary>
    public string? RoleId { get; set; }

    /// <summary>The folder it's in, within its tree, or null for the tree's top level.</summary>
    public Guid? FolderId { get; set; }

    public string LayoutJson { get; set; } = "{\"cells\":[],\"mobileTwoColumn\":false}";

    /// <summary>Auto-advance interval for a multi-view tour started from Pages/Views/Index. 0 means
    /// this view isn't part of a tour rotation (its own interval doesn't apply).</summary>
    public int SequenceIntervalSeconds { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>A folder in a view tree: a role's shared tree (<see cref="RoleId"/>) or one user's
/// Private tree (<see cref="UserId"/>) — exactly one is set. Folders nest without limit through
/// <see cref="ParentId"/> (null at the tree's top level). Deleting one moves what's in it up a level.</summary>
public class ViewFolder
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public string? RoleId { get; set; }
    public string? UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
}

/// <summary>Lets members of <see cref="GrantedRoleId"/> open (not change) every view in
/// <see cref="OwnerRoleId"/>'s tree. Only an admin sets these (Settings → View access): roles can't
/// share their own views, so nobody inside a role can show them to a role that shouldn't see them.</summary>
public class ViewTreeGrant
{
    public Guid Id { get; set; }
    public string OwnerRoleId { get; set; } = string.Empty;
    public string GrantedRoleId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
}
