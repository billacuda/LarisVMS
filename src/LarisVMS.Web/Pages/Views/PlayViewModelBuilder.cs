using System.Security.Claims;
using LarisVMS.Core;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Helpers;

namespace LarisVMS.Web.Pages.Views;

/// <summary>Builds the shared <see cref="PlayViewModel"/> for a saved View — the normal (non
/// single-camera, non-tour-redirect) case both `Views/Play/{id}` and `Live/Index` render
/// identically. Extracted from `PlayModel.OnGetAsync`'s own `id != null` branch specifically so
/// `Live/Index` can render this content directly instead of redirecting to `/Views/Play/{id}`
/// (that redirect was why the sidebar's "Live" item never highlighted — the browser genuinely
/// ended up on a `/Views/Play/...` URL). Callers still own the single-camera path and the
/// tour-redirect path themselves; this only covers "show me View X".</summary>
public sealed class PlayViewModelBuilder(IViewService viewService, ICameraService cameraService,
    IAuditService auditService, ISettingsResolver settings, ICameraAccessService cameraAccess)
{
    /// <summary>Null means the view doesn't exist or isn't visible to this user — caller should
    /// redirect to the views list, same as `PlayModel.OnGetAsync` always has.</summary>
    public async Task<PlayViewModel?> BuildAsync(Guid viewId, ClaimsPrincipal user, string? remoteIp,
        CancellationToken ct = default)
    {
        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

        var view = await viewService.GetVisibleToAsync(viewId, userId);
        if (view is null) return null;

        var all = await cameraService.ListAsync(ct);
        var cameras = all.Where(c => c.IsEnabled && c.NodeId is not null).ToList();
        var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(user, CameraAccessActions.View, ct);
        if (accessible is not null) cameras = cameras.Where(c => accessible.Contains(c.Id)).ToList();

        var views = await viewService.ListVisibleToAsync(userId);
        var badgeCorner = EventBadgeCorner.Normalize(
            await settings.GetRawAsync(EventSettingsKeys.EventBadgeCornerKey));
        var adaptiveStreaming = await settings.GetAsync("LiveView.AdaptiveStreamingEnabled", false);

        // Names resolved from the view's own layout, not from `cameras` above — that list is every
        // enabled camera on the system (it feeds the client-side player), not this view's own set,
        // so using it would log every camera in the deployment on every view opened.
        var viewCameraNames = ViewLayout.CameraIds(view.LayoutJson)
            .Select(vc => all.FirstOrDefault(c => c.Id == vc)?.Name ?? vc.ToString())
            .ToList();
        await auditService.LogAsync("View.Watch", userId, user.Identity?.Name, remoteIp,
            viewCameraNames.Count == 0
                ? $"View '{view.Name}' (no cameras)"
                : $"View '{view.Name}': {string.Join(", ", viewCameraNames)}", ct);

        return new PlayViewModel
        {
            Name = view.Name,
            LayoutJson = view.LayoutJson,
            CurrentViewId = view.Id,
            Cameras = cameras,
            Views = views,
            EventBadgeCornerValue = badgeCorner,
            AdaptiveStreamingEnabled = adaptiveStreaming,
            // Always computed, even off the tour path — a tour stop reached via a direct link
            // rather than GetTourViewsAsync still needs to advance eventually rather than sitting
            // on this view forever, same fallback PlayModel's own tour branch always used.
            TourIntervalSeconds = view.SequenceIntervalSeconds > 0 ? view.SequenceIntervalSeconds : 15,
        };
    }
}
