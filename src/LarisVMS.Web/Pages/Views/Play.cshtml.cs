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
    ISettingsResolver settings, ICameraAccessService cameraAccess, PlayViewModelBuilder playViewModelBuilder) : PageModel
{
    public PlayViewModel Vm { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid? id, Guid? cameraId, bool tour, int i)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

        if (id is null && cameraId is not null)
        {
            // Resolved here rather than via the shared builder (which only covers "show me saved
            // View X") — the single-camera picker needs no saved View, just this app's usual
            // CameraAccess-narrowed feed, same enforcement Cameras gets everywhere else.
            var all = await cameraService.ListAsync();
            var cameras = all.Where(c => c.IsEnabled && c.NodeId is not null).ToList();
            var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(User, CameraAccessActions.View);
            if (accessible is not null) cameras = cameras.Where(c => accessible.Contains(c.Id)).ToList();

            var camera = cameras.FirstOrDefault(c => c.Id == cameraId.Value);
            if (camera is null) return RedirectToPage("Index");

            await auditService.LogAsync("Camera.WatchSingle", userId, User.Identity?.Name,
                HttpContext.Connection.RemoteIpAddress?.ToString(), camera.Name);

            Vm = new PlayViewModel
            {
                Name = camera.Name,
                SingleCameraId = camera.Id,
                Cameras = cameras,
                Views = await viewService.ListVisibleToAsync(userId),
                EventBadgeCornerValue = EventBadgeCorner.Normalize(
                    await settings.GetRawAsync(EventSettingsKeys.EventBadgeCornerKey)),
                AdaptiveStreamingEnabled = await settings.GetAsync("LiveView.AdaptiveStreamingEnabled", false),
            };
            return Page();
        }

        if (id is null)
        {
            if (!tour) return RedirectToPage("Index");
            var tourViews = await viewService.GetTourViewsAsync();
            if (tourViews.Count == 0) return RedirectToPage("Index");
            return RedirectToPage(new { id = tourViews[0].Id, tour = true, i = 0 });
        }

        var vm = await playViewModelBuilder.BuildAsync(id.Value, User, HttpContext.Connection.RemoteIpAddress?.ToString());
        if (vm is null) return RedirectToPage("Index");

        if (tour)
        {
            vm.IsTour = true;
            vm.TourViewIds = (await viewService.GetTourViewsAsync()).Select(v => v.Id).ToList();
            vm.TourIndex = i;
        }

        Vm = vm;
        return Page();
    }
}
