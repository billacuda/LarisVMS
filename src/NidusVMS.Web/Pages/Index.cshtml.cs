using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace NidusVMS.Web.Pages;

[Authorize]
public class IndexModel : PageModel
{
    public void OnGet() { }
}
