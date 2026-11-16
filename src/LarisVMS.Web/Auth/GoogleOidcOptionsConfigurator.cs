using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Auth;

/// <summary>
/// Supplies the "Google" OpenIdConnect scheme's options from GoogleSsoSettings, read fresh from the
/// database the same way <see cref="EntraOidcOptionsConfigurator"/> does for Entra: Security.razor
/// evicts the cached options on save, so a change applies to the next sign-in with no restart.
/// </summary>
public class GoogleOidcOptionsConfigurator(IServiceScopeFactory scopeFactory) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public const string SchemeName = "Google";

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name != SchemeName) return;

        using var scope = scopeFactory.CreateScope();
        var settings = ReadSettings(scope.ServiceProvider);

        // The options are validated on every request, so an unconfigured install still needs a
        // non-empty ClientId. Never used for a real sign-in: the button only shows once enabled,
        // which requires a client ID.
        options.ClientId = !string.IsNullOrWhiteSpace(settings?.ClientId) ? settings.ClientId : "unconfigured";
        options.ClientSecret = settings?.ClientSecret ?? string.Empty;
        options.Authority = "https://accounts.google.com";
        options.ResponseType = "code";
        // Only the sign-in claims are needed; nothing calls Google APIs for the user.
        options.SaveTokens = false;
        options.CallbackPath = "/signin-google";
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("email");
        options.Scope.Add("profile");
    }

    public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);

    /// <summary>Null when there's no database to read yet (before the setup wizard), treated as
    /// "Google not configured".</summary>
    private static GoogleSsoSettings? ReadSettings(IServiceProvider services)
    {
        if (!DatabaseConfig.IsConfigured(services.GetRequiredService<IConfiguration>())) return null;
        try
        {
            return services.GetRequiredService<ApplicationDbContext>().GoogleSsoSettings.AsNoTracking().FirstOrDefault();
        }
        catch (System.Data.Common.DbException)
        {
            return null;
        }
    }
}
