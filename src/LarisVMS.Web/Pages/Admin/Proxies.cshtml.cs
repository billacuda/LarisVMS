using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>Failover plan phase 2: CRUD for the standalone relay tier. A proxy self-registers via
/// <c>install-proxy.ps1</c> (reusing the node registration key); this page is where an admin then
/// sets its routable host / expected port / certificate and enables it. Node → proxy assignment
/// lives on <c>Admin/Nodes</c>. Gated by <c>Nodes.Edit</c>, same as the Nodes page.</summary>
[Authorize("Nodes.Edit")]
public class ProxiesModel(IProxyService proxyService, INodeService nodeService, ISettingsResolver settings,
    IAuditService auditService) : PageModel
{
    public List<MediaProxy> Proxies { get; set; } = [];
    public Dictionary<System.Guid, int> AssignedNodeCount { get; set; } = [];
    public string? RegistrationKey { get; set; }
    public string? ErrorMessage { get; set; }

    [TempData] public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        Proxies = await proxyService.ListAsync();
        RegistrationKey = await settings.GetRawAsync("Node.RegistrationKey");

        var nodes = await nodeService.ListAsync();
        foreach (var n in nodes)
        {
            if (n.PrimaryProxyId is { } p) AssignedNodeCount[p] = AssignedNodeCount.GetValueOrDefault(p) + 1;
            if (n.BackupProxyId is { } b) AssignedNodeCount[b] = AssignedNodeCount.GetValueOrDefault(b) + 1;
        }
    }

    public async Task<IActionResult> OnPostUpdateAsync(System.Guid id, string name, string host, int port, bool enabled,
        string? certPfxPath, string? certPfxPassword, string? allowInsecure)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                ErrorMessage = "A routable host is required — this is the FQDN browsers dial and it must match the proxy's certificate.";
                await OnGetAsync();
                return Page();
            }
            if (port is < 1 or > 65535)
            {
                ErrorMessage = "Port must be between 1 and 65535.";
                await OnGetAsync();
                return Page();
            }

            bool? insecure = allowInsecure switch { "true" => true, "false" => false, _ => null };
            var before = (await proxyService.ListAsync()).FirstOrDefault(p => p.Id == id);

            await proxyService.UpdateAsync(id, name, host, port, enabled,
                certPfxPath, string.IsNullOrEmpty(certPfxPassword) ? null : certPfxPassword, insecure);

            var details = AuditDiff.Build(
                AuditDiff.Of("Name", before?.Name, name),
                AuditDiff.Of("Host", before?.Host, host),
                AuditDiff.Of("Port", before?.Port.ToString(), port.ToString()),
                AuditDiff.Of("Enabled", before?.Enabled.ToString(), enabled.ToString()),
                AuditDiff.Of("Cert pfx path", before?.CertPfxPath, certPfxPath),
                AuditDiff.Of("Allow insecure", before?.AllowInsecure?.ToString(), insecure?.ToString()));

            await LogAsync("Proxy.Update", details is null ? $"{name} ({id})" : $"{name} ({id}) — {details}");
        }
        catch (System.Exception ex)
        {
            ErrorMessage = ex.Message;
            await OnGetAsync();
            return Page();
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(System.Guid id)
    {
        var proxy = (await proxyService.ListAsync()).FirstOrDefault(p => p.Id == id);
        await proxyService.DeleteAsync(id);
        await LogAsync("Proxy.Delete", $"{proxy?.Name ?? "?"} ({id})");
        StatusMessage = "Proxy removed. Any nodes it was assigned to have been unassigned.";
        return RedirectToPage();
    }

    private Task LogAsync(string action, string details) =>
        auditService.LogAsync(action, User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
            User.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString(), details);
}
