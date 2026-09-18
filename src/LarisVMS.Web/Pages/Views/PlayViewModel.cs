using LarisVMS.Core;
using LarisVMS.Core.Entities;

namespace LarisVMS.Web.Pages.Views;

/// <summary>Everything `_PlayContent.cshtml`/`_PlayScripts.cshtml` need to render a view's live
/// grid — shared between `Views/Play` (the dedicated `/Views/Play/{id}` URL) and `Live/Index`
/// (which used to just redirect there; now renders the same content directly so the sidebar's
/// "Live" item highlights correctly). See <see cref="PlayViewModelBuilder"/> for how the normal
/// (non-single-camera) case is built.</summary>
public sealed class PlayViewModel
{
    public string Name { get; set; } = string.Empty;
    public string LayoutJson { get; set; } = "{\"cells\":[],\"mobileTwoColumn\":false}";
    public List<Camera> Cameras { get; set; } = [];
    public Guid CurrentViewId { get; set; }

    /// <summary>All other views the current user can switch to without leaving this page.</summary>
    public List<View> Views { get; set; } = [];

    public string EventBadgeCornerValue { get; set; } = EventBadgeCorner.Default;
    public bool AdaptiveStreamingEnabled { get; set; }

    /// <summary>Set only for the single-camera picker — non-null tells the client to render one ad
    /// hoc tile for this camera instead of parsing LayoutJson.</summary>
    public Guid? SingleCameraId { get; set; }

    public bool IsTour { get; set; }
    public List<Guid> TourViewIds { get; set; } = [];
    public int TourIndex { get; set; }
    public int TourIntervalSeconds { get; set; } = 15;
}
