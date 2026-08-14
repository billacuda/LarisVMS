using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Setup;

public class ReviewModel(ISetupService setupService) : PageModel
{
    public async Task OnGetAsync()
    {
        await setupService.FinalizeSetupAsync();
    }
}
