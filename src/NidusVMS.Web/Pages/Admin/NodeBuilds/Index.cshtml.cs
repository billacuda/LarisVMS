using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Interfaces;

namespace NidusVMS.Web.Pages.Admin.NodeBuilds;

/// <summary>Upload NidusVMS.Node.exe builds for recorder-node auto-update — same Nodes.Edit gate as
/// Admin/Nodes, since this is node administration too, just the "give nodes something new to run"
/// half rather than the "manage already-registered nodes" half.</summary>
// RequestSizeLimit/RequestFormLimits are IAuthorizationFilter/IResourceFilter attributes — Razor
// Pages only honors them at the PageModel class level (MVC1001 if applied to a handler method
// instead; it compiles but silently never takes effect), unlike an MVC controller action where either
// placement works. 300 MB comfortably covers a self-contained single-file NidusVMS.Node.exe (the .NET
// runtime plus this project's own managed code — no ffmpeg bundled, see build-node.ps1) with real
// headroom; both attributes are needed since RequestFormLimits' MultipartBodyLengthLimit (128 MB
// default) is checked separately from — and before — RequestSizeLimit's whole-request cap.
[Authorize("Nodes.Edit")]
[RequestSizeLimit(300 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 300 * 1024 * 1024)]
public class IndexModel(INodeBuildService nodeBuildService) : PageModel
{
    public List<NodeBuildVersion> Builds { get; set; } = [];
    public string? ErrorMessage { get; set; }

    // Not named "File" — PageModel already declares a protected File(...) helper (returns a
    // FileResult), and a same-named property here would hide it (CS0108) for no benefit.
    [BindProperty] public IFormFile? UploadFile { get; set; }
    [BindProperty] public string Version { get; set; } = string.Empty;
    [BindProperty] public string Platform { get; set; } = "win-x64";
    [BindProperty] public string? Notes { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Builds = await nodeBuildService.ListAsync(ct);
    }

    public async Task<IActionResult> OnPostUploadAsync(CancellationToken ct)
    {
        if (UploadFile is null || UploadFile.Length == 0)
        {
            ErrorMessage = "Choose a file to upload.";
        }
        else if (string.IsNullOrWhiteSpace(Version))
        {
            ErrorMessage = "Version is required.";
        }
        else
        {
            await using var stream = UploadFile.OpenReadStream();
            await nodeBuildService.UploadAsync(stream, UploadFile.FileName, Version.Trim(), Platform,
                string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(), ct);
            return RedirectToPage();
        }

        Builds = await nodeBuildService.ListAsync(ct);
        return Page();
    }
}
