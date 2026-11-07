using System.Globalization;
using System.Security.Claims;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Caching.Memory;

namespace LarisVMS.Web.Localization;

/// <summary>
/// Picks each request's UI language and sets <see cref="CultureInfo.CurrentUICulture"/> to it:
/// <list type="number">
/// <item>the signed-in user's own choice (preference <see cref="PreferenceKey"/>);</item>
/// <item>the language cookie (the sign-in page and setup wizard, before anyone is signed in);</item>
/// <item>the server's default language (Settings → Language);</item>
/// <item>the browser's Accept-Language;</item>
/// <item>English.</item>
/// </list>
/// Each choice only counts if that language is installed. Only the UI culture changes:
/// <see cref="CultureInfo.CurrentCulture"/> stays the server's, so numbers written into CSS, JSON,
/// and attributes keep the format the pages already rely on. Dates and times are shown by the
/// browser (local-time.js) in the page's language.
/// </summary>
public sealed class LocaleMiddleware(RequestDelegate next)
{
    public const string PreferenceKey = "ui.language";
    public const string DefaultSettingKey = "Language.Default";
    public const string ShowPseudoSettingKey = "Language.ShowPseudo";
    public static string CookieName => CookieRequestCultureProvider.DefaultCookieName;

    private static readonly TimeSpan PreferenceCacheTime = TimeSpan.FromMinutes(10);

    public async Task InvokeAsync(HttpContext context, LocaleCatalog catalog, IMemoryCache cache)
    {
        var culture = await ResolveAsync(context, catalog, cache);
        CultureInfo ui;
        try { ui = CultureInfo.GetCultureInfo(culture); }
        catch (CultureNotFoundException) { ui = CultureInfo.GetCultureInfo(LocaleCatalog.English); }

        CultureInfo.CurrentUICulture = ui;
        context.Features.Set<IRequestCultureFeature>(new RequestCultureFeature(new RequestCulture(CultureInfo.CurrentCulture, ui), null));
        await next(context);
    }

    /// <summary>Forgets a user's cached choice after they change it.</summary>
    public static void Forget(IMemoryCache cache, string userId) => cache.Remove(CacheKey(userId));

    /// <summary>The cookie that carries an anonymous visitor's choice (and a signed-in user's, so
    /// the sign-in page after signing out stays in their language).</summary>
    public static void WriteCookie(HttpResponse response, string culture) =>
        response.Cookies.Append(CookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax
            });

    private static async Task<string> ResolveAsync(HttpContext context, LocaleCatalog catalog, IMemoryCache cache)
    {
        if (context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } userId)
        {
            var preferred = await cache.GetOrCreateAsync(CacheKey(userId), async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = PreferenceCacheTime;
                try
                {
                    var prefs = context.RequestServices.GetRequiredService<IUserPreferenceService>();
                    return await prefs.GetAsync(userId, PreferenceKey, context.RequestAborted) ?? "";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return "";
                }
            });
            if (catalog.Match(preferred) is { } fromPreference) return fromPreference;
        }

        if (context.Request.Cookies.TryGetValue(CookieName, out var cookie)
            && CookieRequestCultureProvider.ParseCookieValue(cookie) is { } parsed
            && catalog.Match(parsed.UICultures.FirstOrDefault().Value) is { } fromCookie)
            return fromCookie;

        // Pre-setup there may be no database to read a setting from.
        if (!LarisVMS.Web.Middleware.SetupMiddleware.IsSetupPending(context))
        {
            try
            {
                var settings = context.RequestServices.GetRequiredService<ISettingsResolver>();
                var serverDefault = await settings.GetRawAsync(DefaultSettingKey, ct: context.RequestAborted);
                // Background work (alerts, notices) reads it from here; the settings cache makes this cheap.
                Loc.DefaultLanguage = serverDefault;
                if (catalog.Match(serverDefault) is { } fromSetting) return fromSetting;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The database is unreachable: fall through to the browser's language.
            }
        }

        var acceptLanguage = context.Request.GetTypedHeaders().AcceptLanguage;
        if (acceptLanguage is { Count: > 0 })
        {
            foreach (var lang in acceptLanguage.OrderByDescending(l => l.Quality ?? 1))
            {
                var value = lang.Value.Value;
                // The pseudo-locale is for testing on purpose, never picked up from a browser.
                if (value is null || value.StartsWith("qps", StringComparison.OrdinalIgnoreCase)) continue;
                if (catalog.Match(value) is { } fromBrowser) return fromBrowser;
            }
        }

        return LocaleCatalog.English;
    }

    private static string CacheKey(string userId) => $"locale.user.{userId}";
}
