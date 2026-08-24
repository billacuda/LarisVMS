using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Auth;

/// <summary>
/// Supplies the "EntraID" OpenIdConnect scheme's real options (Authority/ClientId/ClientSecret) from
/// EntraSsoSettings, read fresh from the database rather than baked in once at Program.cs startup — the
/// database may not even be reachable yet at boot (a fresh install pre-setup-wizard), and an admin
/// fixing a typo'd client secret shouldn't need to restart the app pool to have it take effect.
///
/// The options pattern normally caches a named options instance after the first time it's resolved
/// (IOptionsMonitorCache), so on its own this would still only ever run once per process — Configure()
/// running per-challenge depends on SecurityModel explicitly evicting the "EntraID" entry from that
/// cache (IOptionsMonitorCache&lt;OpenIdConnectOptions&gt;.TryRemove) whenever it saves a change, forcing
/// the next sign-in attempt to re-resolve options through here with the freshly saved row.
/// </summary>
public class EntraOidcOptionsConfigurator(IServiceScopeFactory scopeFactory) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public const string SchemeName = "EntraID";

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name != SchemeName) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = db.EntraSsoSettings.AsNoTracking().FirstOrDefault();

        // OpenIdConnectOptions.Validate() requires a non-empty ClientId unconditionally — and per
        // this class' own doc comment, AuthenticationMiddleware resolves (and thus validates) every
        // IAuthenticationRequestHandler scheme's options on *every* request, not just a real Entra
        // sign-in attempt. An empty string here (the pre-fix behavior) meant every request in every
        // deployment that hasn't configured Entra yet — i.e. almost all of them — threw before ever
        // reaching a page. Same "harmless, syntactically valid placeholder" reasoning as Authority
        // below: never actually reached with a real sign-in attempt while unconfigured, since
        // Login.cshtml only offers the button once EntraSsoSettings.IsEnabled is true.
        options.ClientId = !string.IsNullOrWhiteSpace(settings?.ClientId)
            ? settings.ClientId
            : "00000000-0000-0000-0000-000000000000";
        options.ClientSecret = settings?.ClientSecret ?? string.Empty;
        options.Authority = !string.IsNullOrWhiteSpace(settings?.TenantId)
            ? $"https://login.microsoftonline.com/{settings.TenantId}/v2.0"
            // Never actually reached with a real sign-in attempt while unconfigured — Login.cshtml
            // only offers the button once EntraSsoSettings.IsEnabled is true, which requires a
            // TenantId to have been saved. A harmless, syntactically valid placeholder rather than
            // leaving Authority blank (which OpenIdConnectHandler would reject outright at startup).
            : "https://login.microsoftonline.com/common/v2.0";
        options.ResponseType = "code";
        // This app only needs the sign-in claims (email/name) — it never calls Graph or any other
        // Entra-protected API on the signed-in user's own behalf, so there's no token worth persisting.
        options.SaveTokens = false;
        options.CallbackPath = "/signin-oidc";
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("email");
        options.Scope.Add("profile");
    }

    public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);
}
