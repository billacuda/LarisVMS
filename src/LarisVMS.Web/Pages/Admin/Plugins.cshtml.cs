using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>
/// What camera integration providers this build ships and which cameras are actually using them.
/// Read-only by design: providers are compiled in and listed in <see cref="CameraIntegrations.All"/>
/// (see that type's own doc comment for why they aren't loaded from external assemblies), and a
/// camera's provider is chosen automatically from the make and model reported during probing — there
/// is nothing here for an admin to install, enable, or configure. The page exists to answer "what's
/// running, at what version, and on which cameras", which previously required reading the source.
/// </summary>
[Authorize("Plugins.View")]
public class PluginsModel(ICameraService cameraService) : PageModel
{
    public record PluginRow(string Key, string DisplayName, string Version, string Summary, List<string> CameraNames);

    public List<PluginRow> Plugins { get; set; } = [];

    /// <summary>Cameras whose stored IntegrationKey matches no provider in this build — a real
    /// possibility the registry explicitly tolerates (a downgrade, or a provider removed in a later
    /// version), which degrades to "no integration" silently at runtime. Surfaced here because
    /// silent is exactly what makes it hard to notice.</summary>
    public List<string> OrphanedCameraNames { get; set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var cameras = await cameraService.ListAsync(ct);

        Plugins = CameraIntegrations.All
            .Select(p => new PluginRow(p.Key, p.DisplayName, p.Version, p.Summary,
                cameras.Where(c => string.Equals(c.IntegrationKey, p.Key, StringComparison.OrdinalIgnoreCase))
                       .Select(c => c.Name)
                       .OrderBy(n => n)
                       .ToList()))
            .ToList();

        OrphanedCameraNames = cameras
            .Where(c => !string.IsNullOrWhiteSpace(c.IntegrationKey) && CameraIntegrations.ByKey(c.IntegrationKey) is null)
            .Select(c => c.Name)
            .OrderBy(n => n)
            .ToList();
    }
}
