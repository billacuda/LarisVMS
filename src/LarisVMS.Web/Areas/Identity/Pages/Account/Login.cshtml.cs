using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Localization;
using LarisVMS.Infrastructure.ActiveDirectory;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Auth;

namespace LarisVMS.Web.Areas.Identity.Pages.Account;

/// <summary>
/// Local override of the packaged Identity UI's default Login page. Two reasons: the default renders a
/// "Sign in with {scheme}" button for every *registered* external authentication scheme
/// unconditionally, but Entra should only be offered once an admin has actually configured and
/// enabled it (EntraSsoSettings.IsEnabled); and sign-in names are routed by shape — an email address
/// is a local account, anything without "@" is an Active Directory sAMAccountName. An account with
/// two-factor sign-in on goes on to LoginWith2fa (Active Directory ones too). Otherwise deliberately
/// smaller than the full default template: no separate Lockout page.
/// </summary>
[AllowAnonymous]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("sign-in")]
public class LoginModel(
    LarisSignInManager signInManager,
    UserManager<ApplicationUser> userManager,
    ApplicationDbContext db,
    IActiveDirectoryService directory,
    ActiveDirectoryUserSync adSync,
    LarisVMS.Core.Interfaces.IAuditService audit,
    LarisVMS.Web.Services.INotificationService notifications,
    LarisVMS.Web.Services.SignInThrottle throttle,
    ILogger<LoginModel> logger) : PageModel
{
    // Failed sign-ins from one address within a minute before Super Admins hear about it; the
    // sign-in rate limit (10 a minute) means this is someone hitting it.
    private const int FailureAlertThreshold = 10;

    // One lockout notice per account for as long as a lockout lasts, however often it's retried.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> LockoutNoticeSent = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LockoutNoticeInterval = TimeSpan.FromMinutes(15);

    [BindProperty] public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    [TempData] public string? ErrorMessage { get; set; }

    public bool ShowEntraButton { get; set; }

    // The password was right and the account has two-factor sign-in on: the code comes next.
    private bool _needsCode;
    public bool AdEnabled { get; set; }
    public static string EntraSchemeName => EntraOidcOptionsConfigurator.SchemeName;

    public class InputModel
    {
        // Language keys: AddDataAnnotationsLocalization looks them up.
        [Required(ErrorMessage = "account.login.usernameRequired"), Display(Name = "account.login.emailOrUsername")]
        public string Username { get; set; } = string.Empty;
        [Required(ErrorMessage = "account.login.passwordRequired"), DataType(DataType.Password), Display(Name = "common.password")]
        public string Password { get; set; } = string.Empty;
        [Display(Name = "account.login.rememberMe")] public bool RememberMe { get; set; }
    }

    public async Task OnGetAsync(string? returnUrl = null)
    {
        if (!string.IsNullOrEmpty(ErrorMessage)) ModelState.AddModelError(string.Empty, ErrorMessage);

        // Only a page of this site: anything else (or a malformed address) goes to the start page.
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Content("~/");
        // Clears any half-finished external sign-in attempt (e.g. the user navigated back mid-flow)
        // — same cleanup the default page does before showing the form fresh.
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        await LoadPageStateAsync();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        // Only a page of this site: anything else (or a malformed address) goes to the start page.
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Content("~/");
        var adSettings = await LoadPageStateAsync();
        if (!ModelState.IsValid) return Page();

        // Too many wrong tries at this name from this address: turned away before the password is
        // even checked (so Active Directory's own lockout isn't run up either).
        var address = HttpContext.Connection.RemoteIpAddress;
        string? error;
        if (throttle.BlockedFor(Input.Username, address) is { } wait)
        {
            await AuditFailureAsync("Login.Throttled", "Too many wrong tries from this address");
            error = Loc.T("account.login.tooManyTries", new { minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes)) });
        }
        else
        {
            error = AdUsername.IsLocal(Input.Username)
                ? await SignInLocalAsync(adSettings)
                : await SignInActiveDirectoryAsync(adSettings);
            if (error is null) throttle.RecordSuccess(Input.Username, address);
        }
        if (error is null && _needsCode)
            return RedirectToPage("./LoginWith2fa", new { returnUrl = ReturnUrl, rememberMe = Input.RememberMe });
        if (error is null) return LocalRedirect(ReturnUrl);

        ModelState.AddModelError(string.Empty, error);
        return Page();
    }

    private async Task<string?> SignInLocalAsync(ActiveDirectorySettings? adSettings)
    {
        if (!ActiveDirectorySettings.LocalLoginsAllowed(adSettings))
            return Loc.T("account.login.localSignInIsTurned");

        // Super Admins are never locked out by failed sign-ins (anyone could otherwise lock every
        // admin out); the per-address throttle still applies to them.
        var target = await userManager.FindByNameAsync(Input.Username.Trim());
        // A name with no account still takes as long as a wrong password for a real one, so the time
        // the answer takes doesn't say which accounts exist.
        if (target is null)
            userManager.PasswordHasher.VerifyHashedPassword(TimingDummy.User, TimingDummy.Hash(userManager), Input.Password);
        var lockoutOnFailure = target is null || !await IsSuperAdminAsync(target);
        var result = await signInManager.PasswordSignInAsync(Input.Username.Trim(), Input.Password, Input.RememberMe, lockoutOnFailure);
        if (result.Succeeded) return null;
        if (result.RequiresTwoFactor)
        {
            _needsCode = true;
            return null;
        }
        // A locked account gets the same answer as a wrong password, so this page doesn't say which
        // accounts exist.
        if (result.IsLockedOut)
        {
            await AuditFailureAsync("Login.LockedOut", "Local account");
            return Loc.T("account.login.invalidEmailOrPassword");
        }
        throttle.RecordFailure(Input.Username, HttpContext.Connection.RemoteIpAddress);
        await AuditFailureAsync("Login.Failure", "Local account");
        return Loc.T("account.login.invalidEmailOrPassword");
    }

    /// <summary>A password hash made once with the same hasher (and work factor) as real accounts, for
    /// checking a password against when the name has no account.</summary>
    private static class TimingDummy
    {
        public static readonly ApplicationUser User = new() { UserName = "timing-dummy" };
        private static string? _hash;
        public static string Hash(UserManager<ApplicationUser> users) =>
            _hash ??= users.PasswordHasher.HashPassword(User, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)));
    }

    // The username is typed by an anonymous visitor: one line, bounded, before it reaches the log
    // or the audit trail.
    private static string Safe(string value) =>
        new(value.Trim().Take(100).Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    private async Task AuditFailureAsync(string action, string details)
    {
        var username = Safe(Input.Username);
        var ip = HttpContext.Connection.RemoteIpAddress;
        await audit.LogAsync(action, null, username, ip?.ToString(), details, HttpContext.RequestAborted);

        // Every failure counts toward spotting guessing spread over many addresses.
        var spread = action == "Login.Failure" ? LarisVMS.Web.Services.SignInSprayMonitor.Shared.RecordFailure(username, ip) : null;

        // Built per admin, in their language.
        Func<string>? title = null, message = null;
        if (action == "Login.LockedOut")
        {
            var now = DateTime.UtcNow;
            if (LockoutNoticeSent.Count > 1000) LockoutNoticeSent.Clear();
            if (!LockoutNoticeSent.TryGetValue(username, out var sent) || now - sent > LockoutNoticeInterval)
            {
                LockoutNoticeSent[username] = now;
                title = () => Loc.T("account.login.noticeLockedOutTitle");
                message = () => Loc.T("account.login.noticeLockedOut", new { username, ip = ip?.ToString() });
            }
        }
        else if (LarisVMS.Web.Services.AuthFailureLog.Record(logger, "Sign-in", ip, details) == FailureAlertThreshold)
        {
            title = () => Loc.T("account.login.noticeRepeatedTitle");
            message = () => Loc.T("account.login.noticeRepeated", new { count = FailureAlertThreshold, ip = ip?.ToString(), username });
        }
        // Guessing spread over many addresses, which the per-address limits above don't see.
        else if (spread is not null)
        {
            var minutes = (int)LarisVMS.Web.Services.SignInSprayMonitor.Window.TotalMinutes;
            title = () => Loc.T("account.login.noticeSpreadTitle");
            message = spread is LarisVMS.Web.Services.SignInSprayMonitor.AccountNotice a
                ? () => Loc.T("account.login.noticeSpreadAccount", new { count = a.Failures, networks = a.Networks, username = a.Account, minutes })
                : () => Loc.T("account.login.noticeSpreadServer", new { count = spread.Failures, networks = spread.Networks,
                    accounts = ((LarisVMS.Web.Services.SignInSprayMonitor.ServerNotice)spread).Accounts, minutes });
        }
        if (title is null) return;

        // A notice that can't be sent mustn't change what the sign-in page answers.
        try { await notifications.NotifySuperAdminsAsync(title, message!, "/Logs/AuditLogs", HttpContext.RequestAborted); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Could not send the \"{Title}\" notice.", action); }
    }

    private async Task<string?> SignInActiveDirectoryAsync(ActiveDirectorySettings? adSettings)
    {
        if (adSettings is not { IsEnabled: true })
            return Loc.T("account.login.signInWithYourEmail");

        var username = AdUsername.Normalize(Input.Username);

        // An existing account that's locked out (a manual disable, or too many recent failures) is
        // refused before AD is even asked — also what rate-limits password guessing through this page.
        var linked = await FindLinkedUserByNameAsync(username);
        if (linked is not null && await userManager.IsLockedOutAsync(linked))
        {
            await AuditFailureAsync("Login.LockedOut", "Active Directory");
            return Loc.T("account.login.invalidUsernameOrPasswordOr");
        }

        AdUserEntry entry;
        try
        {
            var groupSids = await adSync.GetLinkedGroupSidsAsync(HttpContext.RequestAborted);
            entry = await directory.AuthenticateAsync(adSettings, username, Input.Password, groupSids, HttpContext.RequestAborted);
        }
        catch (AdInvalidCredentialsException)
        {
            throttle.RecordFailure(Input.Username, HttpContext.Connection.RemoteIpAddress);
            if (linked is not null && !await IsSuperAdminAsync(linked)) await userManager.AccessFailedAsync(linked);
            await AuditFailureAsync("Login.Failure", "Active Directory: wrong username or password");
            return Loc.T("account.login.invalidUsernameOrPasswordOr");
        }
        catch (ActiveDirectoryException ex)
        {
            logger.LogWarning(ex, "Active Directory sign-in for {Username} failed: AD unavailable.", Safe(username));
            return Loc.T("account.login.cantReachActiveDirectoryRight");
        }

        var user = await adSync.SyncSignInAsync(entry, HttpContext.RequestAborted);
        if (user is null)
        {
            // The same answer as a wrong password, so this page doesn't reveal that an AD password is
            // right; the reason is in the log.
            var reason = entry.IsActive ? "not in any linked group" : "account disabled or expired in AD";
            logger.LogInformation("Active Directory sign-in for {Username} refused: {Reason}.", Safe(username), reason);
            await AuditFailureAsync("Login.Failure", $"Active Directory: {reason}");
            return Loc.T("account.login.invalidUsernameOrPasswordOr");
        }

        await userManager.ResetAccessFailedCountAsync(user);
        var result = await signInManager.SignInCheckingTwoFactorAsync(user, Input.RememberMe);
        _needsCode = result.RequiresTwoFactor;
        return null;
    }

    private async Task<bool> IsSuperAdminAsync(ApplicationUser user)
    {
        var roleIds = await db.UserRoles.Where(ur => ur.UserId == user.Id).Select(ur => ur.RoleId).ToListAsync();
        return roleIds.Count > 0 && await db.RoleProfiles.AnyAsync(p => roleIds.Contains(p.RoleId) && p.Tag == LarisVMS.Core.Auth.RoleTags.SuperAdmin);
    }

    private async Task<ApplicationUser?> FindLinkedUserByNameAsync(string username)
    {
        var user = await userManager.FindByNameAsync(username);
        if (user is null) return null;
        var isAdUser = await db.UserLogins.AnyAsync(l => l.UserId == user.Id && l.LoginProvider == ActiveDirectoryUserSync.LoginProvider);
        return isAdUser ? user : null;
    }

    private async Task<ActiveDirectorySettings?> LoadPageStateAsync()
    {
        ShowEntraButton = await db.EntraSsoSettings.AsNoTracking().Select(s => s.IsEnabled).FirstOrDefaultAsync();
        var ad = await db.ActiveDirectorySettings.AsNoTracking().FirstOrDefaultAsync();
        AdEnabled = ad?.IsEnabled == true;
        return ad;
    }
}
