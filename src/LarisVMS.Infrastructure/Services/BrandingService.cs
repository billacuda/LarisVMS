using Microsoft.Extensions.Configuration;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Infrastructure.Services;

/// <inheritdoc cref="IBrandingService"/>
public class BrandingService(ISettingsResolver settings, IConfiguration configuration) : IBrandingService
{
    public const string AppNameKey = "Branding.AppName";
    public const string PrimaryColorKey = "Branding.PrimaryColor";
    public const string AccentColorKey = "Branding.AccentColor";
    public const string LogoDataUriKey = "Branding.LogoDataUri";
    public const string FontKeyKey = "Branding.FontKey";

    public async Task<BrandingOptions> GetAsync(CancellationToken ct = default)
    {
        try
        {
            // setup-generated.json is the fallback, not the source of truth: the Setup wizard writes
            // AppName/PrimaryColor there before there's a Settings table worth writing to, and this
            // keeps that first-run branding showing until an admin saves on Admin/Branding (which
            // writes Setting rows that then win). No migration or seeding step needed — the fallback
            // simply stops being consulted once a real row exists.
            var appName = await settings.GetRawAsync(AppNameKey, ct: ct)
                ?? configuration["Branding:AppName"];
            var primary = await settings.GetRawAsync(PrimaryColorKey, ct: ct)
                ?? configuration["Branding:PrimaryColor"];

            return new BrandingOptions(
                Branding.NormalizeAppName(appName),
                Branding.NormalizeColor(primary),
                Branding.NormalizeColor(await settings.GetRawAsync(AccentColorKey, ct: ct)),
                Branding.NormalizeLogoDataUri(await settings.GetRawAsync(LogoDataUriKey, ct: ct)),
                Branding.NormalizeFontKey(await settings.GetRawAsync(FontKeyKey, ct: ct)));
        }
        catch
        {
            // The layout renders on every page including the pre-setup wizard, where there is no
            // schema to read yet — same "fall back rather than fail the page" stance _Layout already
            // takes around its AppVersions lookup.
            return new BrandingOptions(
                Branding.NormalizeAppName(configuration["Branding:AppName"]),
                Branding.NormalizeColor(configuration["Branding:PrimaryColor"]),
                null, null, null);
        }
    }

    public async Task SaveAsync(BrandingOptions options, string? modifiedBy, CancellationToken ct = default)
    {
        // Re-normalized here rather than trusting the caller: this is the only write path, so
        // validating at the boundary guarantees nothing unvalidated can reach storage even if a
        // future caller forgets. Empty string (not null) clears a key, since SetGlobalAsync stores
        // exactly what it's given and GetRawAsync treats an empty value as "unset" downstream via
        // the Normalize* helpers.
        await settings.SetGlobalAsync(AppNameKey, Branding.NormalizeAppName(options.AppName), modifiedBy, ct);
        await settings.SetGlobalAsync(PrimaryColorKey, Branding.NormalizeColor(options.PrimaryColor) ?? "", modifiedBy, ct);
        await settings.SetGlobalAsync(AccentColorKey, Branding.NormalizeColor(options.AccentColor) ?? "", modifiedBy, ct);
        await settings.SetGlobalAsync(LogoDataUriKey, Branding.NormalizeLogoDataUri(options.LogoDataUri) ?? "", modifiedBy, ct);
        await settings.SetGlobalAsync(FontKeyKey, Branding.NormalizeFontKey(options.FontKey) ?? "", modifiedBy, ct);
    }
}
