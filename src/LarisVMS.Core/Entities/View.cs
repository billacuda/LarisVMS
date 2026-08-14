namespace LarisVMS.Core.Entities;

/// <summary>
/// A saved camera wall layout (M6). LayoutJson holds the GridStack cells — see the plan's "Views &
/// layout" section for the shape: <c>{"cells":[{"id","x","y","w","h","aspect","cameraId",
/// "hideOnPhone"}],"mobileTwoColumn":false}</c>. The mobile arrangement is always derived from this
/// at render time (Pages/Views/Play), never stored — a view built on desktop gets a usable phone
/// layout for free and there is nothing to keep in sync.
/// </summary>
public class View
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>ApplicationUser.Id of the creator. Owner can always edit/delete their own view,
    /// shared or not; a shared view can also be edited by anyone with Views.Edit.</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>Visible to every user with Views.View, not just the owner.</summary>
    public bool IsShared { get; set; }

    public string LayoutJson { get; set; } = "{\"cells\":[],\"mobileTwoColumn\":false}";

    /// <summary>Auto-advance interval for a multi-view tour started from Pages/Views/Index. 0 means
    /// this view isn't part of a tour rotation (its own interval doesn't apply).</summary>
    public int SequenceIntervalSeconds { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
