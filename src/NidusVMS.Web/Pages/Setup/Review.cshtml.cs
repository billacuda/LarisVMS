using Microsoft.AspNetCore.Mvc.RazorPages;
using NidusVMS.Core.Interfaces;

namespace NidusVMS.Web.Pages.Setup;

public class ReviewModel(ISetupService setupService) : PageModel
{
    public async Task OnGetAsync()
    {
        await setupService.FinalizeSetupAsync();
    }
}
