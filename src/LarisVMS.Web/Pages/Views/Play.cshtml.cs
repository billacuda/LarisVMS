using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Helpers;

namespace LarisVMS.Web.Pages.Views;

[Authorize("Views.View")]
public class PlayModel(IViewService viewService, ICameraService cameraService, IAuditService auditService,
    ISettingsResolver settings, ICameraAccessService cameraAccess) : PageModel
{
    /// <summary>Where this deployment wants motion/detection badges drawn on a live tile — see
    /// <see cref="EventBadgeCorner"/>. Always an allowlisted value, never whatever happens to be
    /// stored, because it drives positioning classes in the browser.</summary>
    public string EventBadgeCornerValue { get; set; } = EventBadgeCorner.Default;

    /// <summary>M18: the same "LiveView.AdaptiveStreamingEnabled" setting NodeService.GetConfigAsync
    /// resolves for nodes — the client needs it too, to decide whether to ever ask for `?role=sub` at
    /// all (and whether to show the per-tile quality override). Resolved for both the normal View
    /// path and the single-camera path below, same as EventBadgeCornerValue.</summary>
    public bool AdaptiveStreamingEnabled { get; set; } = false;

    public string Name { get; set; } = string.Empty;
    public string LayoutJson { get; set; } = "{\"cells\":[],\"mobileTwoColumn\":false}";
    public List<Camera> Cameras { get; set; } = [];
    public Guid CurrentViewId { get; set; }

    /// <summary>All other views the current user can switch to without leaving this page — the
    /// picker itself lives only on Live/Index (M6), so once a user landed here that was the only
    /// way to reach another view was "Back to views" + pick again; this mirrors that same picker
    /// here so switching views doesn't require leaving live playback.</summary>
    public List<View> Views { get; set; } = [];

    public bool IsTour { get; set; }
    public List<Guid> TourViewIds { get; set; } = [];
    public int TourIndex { get; set; }
    public int TourIntervalSeconds { get; set; }

    /// <summary>Set only for the single-camera picker (new dropdown beside the View picker, explicit
    /// user ask) — non-null tells the client to render one ad hoc tile for this camera instead of
    /// parsing LayoutJson. No saved View is used or required, same "render it directly, don't hunt
    /// for a View that happens to contain it" reasoning as Playback's own deep-link resolution
    /// (playback-player.js's resolveDeepLink). LayoutJson/CurrentViewId stay at their defaults in
    /// this mode; the client only looks at LayoutJson when SingleCameraId is absent.</summary>
    public Guid? SingleCameraId { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid? id, Guid? cameraId, bool tour, int i)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

        // Resolved once, up front, since both the normal View path and the single-camera path need
        // the same CameraAccess-narrowed feed — the picker dropdown lists it, the player consumes it,
        // and (for the single-camera path) authorization for the requested camera is "is it in this
        // list", the same enforcement Cameras already gets everywhere else on this page.
        var all = await cameraService.ListAsync();
        Cameras = all.Where(c => c.IsEnabled && c.NodeId is not null).ToList();
        var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(User, CameraAccessActions.View);
        if (accessible is not null) Cameras = Cameras.Where(c => accessible.Contains(c.Id)).ToList();

        if (id is null && cameraId is not null)
        {
            var camera = Cameras.FirstOrDefault(c => c.Id == cameraId.Value);
            if (camera is null) return RedirectToPage("Index");

            Name = camera.Name;
            SingleCameraId = camera.Id;
            Views = await viewService.ListVisibleToAsync(userId);
            EventBadgeCornerValue = EventBadgeCorner.Normalize(
                await settings.GetRawAsync(Admin.Settings.EventsModel.EventBadgeCornerKey));
            AdaptiveStreamingEnabled = await settings.GetAsync("LiveView.AdaptiveStreamingEnabled", false);

            await auditService.LogAsync("Camera.WatchSingle", userId, User.Identity?.Name,
                HttpContext.Connection.RemoteIpAddress?.ToString(), camera.Name);

            return Page();
        }

        if (id is null)
        {
            if (!tour) return RedirectToPage("Index");
            var tourViews = await viewService.GetTourViewsAsync();
            if (tourViews.Count == 0) return RedirectToPage("Index");
            return RedirectToPage(new { id = tourViews[0].Id, tour = true, i = 0 });
        }

        var view = await viewService.GetVisibleToAsync(id.Value, userId);
        if (view is null) return RedirectToPage("Index");

        Name = view.Name;
        LayoutJson = view.LayoutJson;
        CurrentViewId = view.Id;
        Views = await viewService.ListVisibleToAsync(userId);
        EventBadgeCornerValue = EventBadgeCorner.Normalize(
            await settings.GetRawAsync(Admin.Settings.EventsModel.EventBadgeCornerKey));
        AdaptiveStreamingEnabled = await settings.GetAsync("LiveView.AdaptiveStreamingEnabled", false);

        // Names resolved from the view's own layout, not from the Cameras list above — that list is
        // every enabled camera on the system (it feeds the client-side player), not this view's own
        // set, so using it would log every camera in the deployment on every view opened.
        var viewCameraNames = ViewLayout.CameraIds(view.LayoutJson)
            .Select(vc => all.FirstOrDefault(c => c.Id == vc)?.Name ?? vc.ToString())
            .ToList();
        await auditService.LogAsync("View.Watch", userId, User.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            viewCameraNames.Count == 0
                ? $"View '{view.Name}' (no cameras)"
                : $"View '{view.Name}': {string.Join(", ", viewCameraNames)}");

        IsTour = tour;
        if (tour)
        {
            TourViewIds = (await viewService.GetTourViewsAsync()).Select(v => v.Id).ToList();
            TourIndex = i;
            // A tour stop with no interval of its own (e.g. reached via a direct link rather than
            // GetTourViewsAsync, which only returns views that already have one set) still needs to
            // advance eventually rather than sitting on this view forever.
            TourIntervalSeconds = view.SequenceIntervalSeconds > 0 ? view.SequenceIntervalSeconds : 15;
        }

        return Page();
    }
}
