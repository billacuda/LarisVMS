using Microsoft.AspNetCore.Mvc.RazorPages;
using Rcordr.Core.Interfaces;

namespace Rcordr.Web.Pages.Setup;

public class ReviewModel(ISetupService setupService) : PageModel
{
    public async Task OnGetAsync()
    {
        await setupService.FinalizeSetupAsync();
    }
}
