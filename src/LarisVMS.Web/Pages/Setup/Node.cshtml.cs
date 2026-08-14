using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Setup;

public class NodeModel(ISetupService setupService) : PageModel
{
    public string RegistrationKey { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;

    public async Task OnGetAsync()
    {
        RegistrationKey = await setupService.GetOrCreateNodeRegistrationKeyAsync();
        ServerUrl = $"{Request.Scheme}://{Request.Host}";
    }

    public IActionResult OnPost() => RedirectToPage("Branding");
}
