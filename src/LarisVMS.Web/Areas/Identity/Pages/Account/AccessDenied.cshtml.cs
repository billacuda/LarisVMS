using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Areas.Identity.Pages.Account;

/// <summary>Local override of the packaged Identity UI's AccessDenied page, so its text is in the
/// user's language.</summary>
[AllowAnonymous]
public class AccessDeniedModel : PageModel;
