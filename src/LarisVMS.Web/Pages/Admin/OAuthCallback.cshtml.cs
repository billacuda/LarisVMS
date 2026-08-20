using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Email;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>
/// Ported from rsolva's OAuthCallback (src/rsolva.Web/Pages/Admin) — the redirect target Google (and
/// any future per-user-consent provider) sends the browser back to after the admin approves access.
/// Adapted for EmailSettings being a singleton row: no account id in the state payload, since there's
/// only ever the one row to update. TempData carries the result back to Pages/Admin/Settings/Email
/// rather than rendering anything here — this page is a bounce, not a destination.
/// </summary>
[Authorize("Settings.Edit")]
public class OAuthCallbackModel(ApplicationDbContext db, IDataProtectionProvider dp, OAuthConnectProviderFactory oauthFactory, IAuditService auditService) : PageModel
{
    private readonly IDataProtector _protector = dp.CreateProtector("EmailOAuthState");

    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error))
            return Fail($"OAuth authorization failed: {error}");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return Fail("OAuth authorization failed: missing code or state.");

        EmailOAuthState? payload;
        try
        {
            payload = JsonSerializer.Deserialize<EmailOAuthState>(_protector.Unprotect(state));
        }
        catch
        {
            return Fail("OAuth authorization failed: invalid or expired request.");
        }

        if (payload is null || DateTime.UtcNow - payload.IssuedAtUtc > TimeSpan.FromMinutes(10))
            return Fail("OAuth authorization failed: the request expired — try connecting again.");

        var settings = await db.EmailSettings.FirstOrDefaultAsync(ct);
        if (settings is null || string.IsNullOrWhiteSpace(settings.GmailClientId) || string.IsNullOrWhiteSpace(settings.GmailClientSecret))
            return Fail("OAuth authorization failed: the Gmail Client ID/Secret are missing — save them first.");

        var redirectUri = Url.Page("/Admin/OAuthCallback", pageHandler: null, values: null, protocol: Request.Scheme, host: Request.Host.Value)!;
        var connectProvider = oauthFactory.Get(payload.Provider);

        string refreshToken;
        try
        {
            refreshToken = await connectProvider.ExchangeCodeForRefreshTokenAsync(
                settings.GmailClientId, settings.GmailClientSecret, code, redirectUri, ct);
        }
        catch (Exception ex)
        {
            return Fail($"OAuth authorization failed: {ex.Message}");
        }

        settings.GmailRefreshToken = refreshToken;
        settings.LastModifiedAt = DateTime.UtcNow;
        settings.LastModifiedBy = User.Identity?.Name;
        await db.SaveChangesAsync(ct);

        await auditService.LogAsync("Settings.EmailGmailConnected",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(), settings.GmailEmailAddress, ct);

        TempData["SavedMessage"] = $"Connected to Gmail as {settings.GmailEmailAddress}.";
        return RedirectToPage("/Admin/Settings/Email");
    }

    private IActionResult Fail(string message)
    {
        TempData["ErrorMessage"] = message;
        return RedirectToPage("/Admin/Settings/Email");
    }
}
