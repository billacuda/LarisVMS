using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Email;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>
/// M15: the outbound-email config the alert evaluator (a later M15 pass) will send through.
/// EmailSettings is a singleton row — GetOrCreateAsync always returns/creates the one row rather than
/// this page needing an id in its route. SmtpPassword/GraphClientSecret are never re-populated into
/// the form on load (same "blank means unchanged" convention as Camera.Password on
/// Pages/Cameras/Edit) since they're encrypted at rest and this page has no reason to ever decrypt
/// them back to a browser. Only the selected Provider's fields are validated/required — the other
/// provider's fields stay in the form (and get saved) so switching back and forth doesn't lose data
/// already entered, but they're not gating whether the save succeeds. GmailRefreshToken has no bind
/// property at all — it's written only by Pages/Admin/OAuthCallback after a successful consent
/// round-trip, never typed directly, so a resave here can never accidentally clear it.
/// </summary>
[Authorize("Settings.Edit")]
public class EmailModel(ApplicationDbContext db, IEmailService emailService, IAuditService auditService,
    OAuthConnectProviderFactory oauthFactory, IDataProtectionProvider dp) : PageModel
{
    private readonly IDataProtector _protector = dp.CreateProtector("EmailOAuthState");

    [BindProperty] public bool IsEnabled { get; set; }
    [BindProperty] public EmailProviderType Provider { get; set; } = EmailProviderType.Smtp;
    [BindProperty] public string FromAddress { get; set; } = string.Empty;
    [BindProperty] public string FromName { get; set; } = string.Empty;

    [BindProperty] public string SmtpHost { get; set; } = string.Empty;
    [BindProperty] public int SmtpPort { get; set; } = 587;
    [BindProperty] public bool SmtpUseSsl { get; set; } = true;
    [BindProperty] public string? SmtpUsername { get; set; }
    [BindProperty] public string? SmtpPassword { get; set; }
    public bool HasStoredSmtpPassword { get; private set; }

    [BindProperty] public string GraphTenantId { get; set; } = string.Empty;
    [BindProperty] public string GraphClientId { get; set; } = string.Empty;
    [BindProperty] public string? GraphClientSecret { get; set; }
    [BindProperty] public string? GraphSharedMailbox { get; set; }
    public bool HasStoredGraphClientSecret { get; private set; }

    [BindProperty] public string GmailClientId { get; set; } = string.Empty;
    [BindProperty] public string? GmailClientSecret { get; set; }
    [BindProperty] public string? GmailEmailAddress { get; set; }
    public bool HasStoredGmailClientSecret { get; private set; }
    public bool IsGmailConnected { get; private set; }

    [BindProperty] public string? TestRecipient { get; set; }

    [TempData] public string? SavedMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(FromAddress) || !new EmailAddressAttribute().IsValid(FromAddress))
        {
            ErrorMessage = "Enter a valid \"from\" email address.";
            await LoadAsync(ct);
            return Page();
        }
        if (IsEnabled)
        {
            var providerError = Provider switch
            {
                EmailProviderType.Smtp when string.IsNullOrWhiteSpace(SmtpHost) =>
                    "An SMTP host is required while email is enabled.",
                EmailProviderType.Graph when string.IsNullOrWhiteSpace(GraphTenantId) || string.IsNullOrWhiteSpace(GraphClientId) =>
                    "A Graph tenant ID and client ID are required while email is enabled.",
                EmailProviderType.Gmail when string.IsNullOrWhiteSpace(GmailClientId) =>
                    "A Gmail Client ID is required while email is enabled.",
                _ => (string?)null
            };
            if (providerError is not null)
            {
                ErrorMessage = providerError;
                await LoadAsync(ct);
                return Page();
            }
        }

        var settings = await GetOrCreateAsync(ct);
        var fields = new List<AuditDiff.Field>
        {
            AuditDiff.Of("Enabled", settings.IsEnabled.ToString(), IsEnabled.ToString()),
            AuditDiff.Of("Provider", settings.Provider.ToString(), Provider.ToString()),
            AuditDiff.Of("FromAddress", settings.FromAddress, FromAddress.Trim()),
            AuditDiff.Of("FromName", settings.FromName, FromName.Trim()),
            AuditDiff.Of("SmtpHost", settings.SmtpHost, SmtpHost.Trim()),
            AuditDiff.Of("SmtpPort", settings.SmtpPort.ToString(), SmtpPort.ToString()),
            AuditDiff.Of("SmtpUseSsl", settings.SmtpUseSsl.ToString(), SmtpUseSsl.ToString()),
            AuditDiff.Of("SmtpUsername", settings.SmtpUsername, SmtpUsername?.Trim()),
            AuditDiff.Of("GraphTenantId", settings.GraphTenantId, GraphTenantId.Trim()),
            AuditDiff.Of("GraphClientId", settings.GraphClientId, GraphClientId.Trim()),
            AuditDiff.Of("GraphSharedMailbox", settings.GraphSharedMailbox, GraphSharedMailbox?.Trim()),
            AuditDiff.Of("GmailClientId", settings.GmailClientId, GmailClientId.Trim()),
            AuditDiff.Of("GmailEmailAddress", settings.GmailEmailAddress, GmailEmailAddress?.Trim())
        };

        settings.IsEnabled = IsEnabled;
        settings.Provider = Provider;
        settings.FromAddress = FromAddress.Trim();
        settings.FromName = FromName.Trim();
        settings.SmtpHost = SmtpHost.Trim();
        settings.SmtpPort = SmtpPort;
        settings.SmtpUseSsl = SmtpUseSsl;
        settings.SmtpUsername = string.IsNullOrWhiteSpace(SmtpUsername) ? null : SmtpUsername.Trim();
        settings.GraphTenantId = string.IsNullOrWhiteSpace(GraphTenantId) ? null : GraphTenantId.Trim();
        settings.GraphClientId = string.IsNullOrWhiteSpace(GraphClientId) ? null : GraphClientId.Trim();
        settings.GraphSharedMailbox = string.IsNullOrWhiteSpace(GraphSharedMailbox) ? null : GraphSharedMailbox.Trim();
        settings.GmailClientId = string.IsNullOrWhiteSpace(GmailClientId) ? null : GmailClientId.Trim();
        settings.GmailEmailAddress = string.IsNullOrWhiteSpace(GmailEmailAddress) ? null : GmailEmailAddress.Trim();

        // Blank means "unchanged" — the form never re-displays a stored secret, so a blank
        // submission only happens when the admin genuinely left it alone.
        if (!string.IsNullOrWhiteSpace(SmtpPassword))
        {
            fields.Add(AuditDiff.SecretChanged("SmtpPassword", true));
            settings.SmtpPassword = SmtpPassword;
        }
        if (!string.IsNullOrWhiteSpace(GraphClientSecret))
        {
            fields.Add(AuditDiff.SecretChanged("GraphClientSecret", true));
            settings.GraphClientSecret = GraphClientSecret;
        }
        if (!string.IsNullOrWhiteSpace(GmailClientSecret))
        {
            fields.Add(AuditDiff.SecretChanged("GmailClientSecret", true));
            settings.GmailClientSecret = GmailClientSecret;
        }
        settings.LastModifiedAt = DateTime.UtcNow;
        settings.LastModifiedBy = User.Identity?.Name;

        await db.SaveChangesAsync(ct);

        var details = AuditDiff.Build([.. fields]);
        if (details is not null)
            await auditService.LogAsync("Settings.EmailUpdate", CurrentUserId, CurrentUserName, RemoteIp, details, ct);

        SavedMessage = "Saved.";
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostTestSendAsync(CancellationToken ct)
    {
        // Save first — a test send should exercise exactly what OnPostSaveAsync just persisted, not
        // an unsaved edit that a browser back-button could otherwise make look like it "worked" for a
        // config that was never actually stored.
        var saveResult = await OnPostSaveAsync(ct);
        if (ErrorMessage is not null) return saveResult;

        if (string.IsNullOrWhiteSpace(TestRecipient) || !new EmailAddressAttribute().IsValid(TestRecipient))
        {
            ErrorMessage = "Enter a valid recipient address to send the test to.";
            await LoadAsync(ct);
            return Page();
        }

        try
        {
            await emailService.SendAsync(TestRecipient.Trim(), "LarisVMS test email",
                "<p>This is a test email from LarisVMS to verify this account's outbound configuration.</p>");
            await auditService.LogAsync("Settings.EmailTestSendSucceeded", CurrentUserId, CurrentUserName, RemoteIp, TestRecipient.Trim(), ct);
            SavedMessage = $"Test email sent to {TestRecipient.Trim()}.";
        }
        catch (Exception ex)
        {
            await auditService.LogAsync("Settings.EmailTestSendFailed", CurrentUserId, CurrentUserName, RemoteIp,
                $"{TestRecipient.Trim()}: {ex.Message}", ct);
            ErrorMessage = $"Test email failed: {ex.Message}";
        }

        await LoadAsync(ct);
        return Page();
    }

    /// <summary>Saves the form first (same reasoning as test-send), then redirects the browser to
    /// Google's consent screen. Requires the Client ID/Secret to already be on the row — Google needs
    /// them to be told apart from any other app, so there's nothing to connect until they're saved.</summary>
    public async Task<IActionResult> OnPostConnectGmailAsync(CancellationToken ct)
    {
        var saveResult = await OnPostSaveAsync(ct);
        if (ErrorMessage is not null) return saveResult;

        var settings = await db.EmailSettings.FirstOrDefaultAsync(ct);
        if (settings is null || string.IsNullOrWhiteSpace(settings.GmailClientId) || string.IsNullOrWhiteSpace(settings.GmailClientSecret))
        {
            ErrorMessage = "Enter a Gmail Client ID and Client Secret and save before connecting.";
            await LoadAsync(ct);
            return Page();
        }

        var redirectUri = Url.Page("/Admin/OAuthCallback", pageHandler: null, values: null, protocol: Request.Scheme, host: Request.Host.Value)!;
        var state = _protector.Protect(JsonSerializer.Serialize(new EmailOAuthState(EmailProviderType.Gmail, DateTime.UtcNow)));
        var authorizationUrl = oauthFactory.Get(EmailProviderType.Gmail).BuildAuthorizationUrl(settings.GmailClientId, redirectUri, state);
        return Redirect(authorizationUrl);
    }

    private async Task<EmailSettings> GetOrCreateAsync(CancellationToken ct)
    {
        var settings = await db.EmailSettings.FirstOrDefaultAsync(ct);
        if (settings is not null) return settings;

        settings = new EmailSettings { Id = Guid.NewGuid() };
        db.EmailSettings.Add(settings);
        return settings;
    }

    private string? CurrentUserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    private string? CurrentUserName => User.Identity?.Name;
    private string? RemoteIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private async Task LoadAsync(CancellationToken ct)
    {
        var settings = await db.EmailSettings.FirstOrDefaultAsync(ct);
        if (settings is null) return;

        IsEnabled = settings.IsEnabled;
        Provider = settings.Provider;
        FromAddress = settings.FromAddress;
        FromName = settings.FromName;
        SmtpHost = settings.SmtpHost ?? string.Empty;
        SmtpPort = settings.SmtpPort;
        SmtpUseSsl = settings.SmtpUseSsl;
        SmtpUsername = settings.SmtpUsername;
        HasStoredSmtpPassword = !string.IsNullOrEmpty(settings.SmtpPassword);
        GraphTenantId = settings.GraphTenantId ?? string.Empty;
        GraphClientId = settings.GraphClientId ?? string.Empty;
        GraphSharedMailbox = settings.GraphSharedMailbox;
        HasStoredGraphClientSecret = !string.IsNullOrEmpty(settings.GraphClientSecret);
        GmailClientId = settings.GmailClientId ?? string.Empty;
        GmailEmailAddress = settings.GmailEmailAddress;
        HasStoredGmailClientSecret = !string.IsNullOrEmpty(settings.GmailClientSecret);
        IsGmailConnected = !string.IsNullOrEmpty(settings.GmailRefreshToken);
    }
}
