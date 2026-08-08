using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Rcordr.Web.Pages;

[Authorize]
public class IndexModel : PageModel
{
    public void OnGet() { }
}
