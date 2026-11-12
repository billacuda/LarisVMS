using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Areas.Identity.Pages.Account;

/// <summary>Where you're signed in, and signing out any of those (or all but this one).</summary>
[Authorize]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("per-user")]
public class SessionsModel(
    UserManager<ApplicationUser> userManager,
    UserSessionService sessions,
    IAuditService audit) : PageModel
{
    public IReadOnlyList<UserSession> Sessions { get; private set; } = [];
    public Guid? Current { get; private set; }
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var userId = userManager.GetUserId(User);
        if (userId is null) return Challenge();
        await LoadAsync(userId);
        return Page();
    }

    public async Task<IActionResult> OnPostEndAsync(Guid id)
    {
        var userId = userManager.GetUserId(User);
        if (userId is null) return Challenge();
        // This one is ended by signing out.
        if (id != UserSessionService.Current(HttpContext) && await sessions.EndAsync(id, userId))
        {
            await audit.LogAsync("User.SessionEnded", userId, User.Identity?.Name, Ip, $"Session {id}", HttpContext.RequestAborted);
            Message = Loc.T("account.sessions.ended");
        }
        await LoadAsync(userId);
        return Page();
    }

    public async Task<IActionResult> OnPostEndOthersAsync()
    {
        var userId = userManager.GetUserId(User);
        if (userId is null) return Challenge();
        var ended = await sessions.EndAllAsync(userId, keep: UserSessionService.Current(HttpContext));
        await audit.LogAsync("User.SessionEnded", userId, User.Identity?.Name, Ip, $"All other sessions ({ended})", HttpContext.RequestAborted);
        Message = Loc.T("account.sessions.endedOthers");
        await LoadAsync(userId);
        return Page();
    }

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private async Task LoadAsync(string userId)
    {
        Current = UserSessionService.Current(HttpContext);
        Sessions = await sessions.ListAsync(userId);
    }
}
