using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>
/// Site branding — app name, colors, logo, font. Same Settings.Edit gate and same
/// ISettingsResolver-backed storage as Admin/Settings, so branding is ordinary editable config
/// rather than the one-time Setup-wizard value it used to be (that value still applies as the
/// fallback until something is saved here).
///
/// The logo is stored as a data URI in the setting row itself rather than as an uploaded file: it's
/// small, it needs no new filesystem plumbing or write permissions, and it can't be orphaned by a
/// redeploy the way a file under the site directory could.
/// </summary>
[Authorize("Settings.Edit")]
public class BrandingModel(IBrandingService branding, IAuditService auditService) : PageModel
{
    [BindProperty] public string AppName { get; set; } = BrandingOptions.DefaultAppName;
    [BindProperty] public string? PrimaryColor { get; set; }
    [BindProperty] public string? AccentColor { get; set; }
    [BindProperty] public string? FontKey { get; set; }

    /// <summary>The already-stored logo, round-tripped through the form so a save that doesn't touch
    /// the logo keeps it. Replaced by the file picker's client-side base64 conversion when a new
    /// image is chosen, and cleared by the Remove checkbox below.</summary>
    [BindProperty] public string? LogoDataUri { get; set; }

    [BindProperty] public bool RemoveLogo { get; set; }

    public string? SavedMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public IReadOnlyList<(string Key, string Label)> FontChoices => Branding.FontChoices;
    public int MaxLogoBytes => Branding.MaxLogoDataUriLength;

    public async Task OnGetAsync()
    {
        var current = await branding.GetAsync();
        AppName = current.AppName;
        PrimaryColor = current.PrimaryColor;
        AccentColor = current.AccentColor;
        LogoDataUri = current.LogoDataUri;
        FontKey = current.FontKey;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var before = await branding.GetAsync();

        // Validated here as well as in the service so the form can actually tell the user *which*
        // field it rejected, rather than silently dropping it to null on save.
        if (!string.IsNullOrWhiteSpace(PrimaryColor) && Branding.NormalizeColor(PrimaryColor) is null)
            ErrorMessage = "Primary color must be a hex value like #0d6efd.";
        else if (!string.IsNullOrWhiteSpace(AccentColor) && Branding.NormalizeColor(AccentColor) is null)
            ErrorMessage = "Accent color must be a hex value like #212529.";
        else if (!RemoveLogo && !string.IsNullOrWhiteSpace(LogoDataUri) && Branding.NormalizeLogoDataUri(LogoDataUri) is null)
            ErrorMessage = $"The logo must be a PNG, JPEG, GIF or WebP image under {MaxLogoBytes / 1024}KB. " +
                "SVG isn't accepted, since an SVG can carry script.";

        if (ErrorMessage is not null) return Page();

        var updated = new BrandingOptions(AppName, PrimaryColor, AccentColor,
            RemoveLogo ? null : LogoDataUri, FontKey);
        await branding.SaveAsync(updated, User.Identity?.Name);

        // The logo is diffed as changed-or-not rather than by value — a base64 image in the audit
        // trail's Details column would be unreadable and enormous.
        var details = AuditDiff.Build(
            AuditDiff.Of("App name", before.AppName, Branding.NormalizeAppName(AppName)),
            AuditDiff.Of("Primary color", before.PrimaryColor, Branding.NormalizeColor(PrimaryColor)),
            AuditDiff.Of("Accent color", before.AccentColor, Branding.NormalizeColor(AccentColor)),
            AuditDiff.Of("Font", before.FontKey, Branding.NormalizeFontKey(FontKey)),
            AuditDiff.Of("Logo", before.LogoDataUri is null ? "(none)" : "(set)",
                updated.LogoDataUri is null ? "(none)" : "(set)"));

        await auditService.LogAsync("Branding.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
            User.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        SavedMessage = "Branding saved.";
        await OnGetAsync();
        return Page();
    }
}
