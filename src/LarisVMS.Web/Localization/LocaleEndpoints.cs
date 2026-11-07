using System.Security.Claims;
using System.Text;
using System.Text.Json;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace LarisVMS.Web.Localization;

/// <summary>
/// Language endpoints: switching language (anyone, including the sign-in page), the script-side text
/// for a language, and the admin Language page's template download, upload, and delete.
/// </summary>
public static class LocaleEndpoints
{
    public const string AdminPage = "/Admin/Settings/Language";

    public static void MapLocaleEndpoints(this WebApplication app)
    {
        // The language picker posts here. Anonymous so the sign-in page and setup wizard can switch
        // too; the form's antiforgery token is checked as for any form post.
        app.MapPost("/locale", async ([FromForm] string culture, [FromForm] string? returnUrl, HttpContext ctx,
            LocaleCatalog catalog, IMemoryCache cache, IServiceProvider services, CancellationToken ct) =>
        {
            var matched = catalog.Match(culture) ?? LocaleCatalog.English;
            LocaleMiddleware.WriteCookie(ctx.Response, matched);
            if (ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } userId)
            {
                try
                {
                    await services.GetRequiredService<IUserPreferenceService>().SetAsync(userId, LocaleMiddleware.PreferenceKey, matched, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Pre-setup or the database is down: the cookie alone still switches language.
                }
                LocaleMiddleware.Forget(cache, userId);
            }
            return Results.LocalRedirect(SafeReturnUrl(returnUrl));
        }).AllowAnonymous();

        // Script-side text: everything under "js." for one language, with fallbacks already applied,
        // read by wwwroot/js/i18n.js. A same-origin script, so it works under the strict CSP. The
        // URL carries the catalog's version, so it can be cached for good.
        app.MapGet("/locales/{culture}.js", (string culture, string? v, HttpContext ctx, LocaleCatalog catalog) =>
        {
            var matched = catalog.Match(culture) ?? LocaleCatalog.English;
            var payload = JsonSerializer.Serialize(new { lang = matched, strings = catalog.Resolve(matched, "js.") });
            ctx.Response.Headers.CacheControl = v == catalog.Version ? "public, max-age=31536000, immutable" : "no-cache";
            return Results.Text($"window.larisvmsI18nData={payload};", "text/javascript", Encoding.UTF8);
        }).AllowAnonymous();

        // The English text, to translate from.
        app.MapGet("/admin/locales/template.json", (LocaleCatalog catalog) =>
        {
            var template = new Dictionary<string, object>
            {
                ["_meta"] = new { name = "", author = "", baseVersion = typeof(LocaleEndpoints).Assembly.GetName().Version?.ToString(3) }
            };
            foreach (var (k, text) in catalog.EnglishEntries.OrderBy(e => e.Key, StringComparer.Ordinal)) template[k] = text;
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(template, IndentedUnescaped), "application/json", "en.json");
        }).RequireAuthorization("Settings.Edit");

        // An installed language as the server sees it (shipped and dropped-in text merged), with any
        // keys it's missing filled in from English, so a translator can pick up where it left off.
        app.MapGet("/admin/locales/{culture}.json", (string culture, LocaleCatalog catalog) =>
        {
            var matched = catalog.Match(culture);
            if (matched is null || matched == LocaleCatalog.Pseudo) return Results.NotFound();
            var info = catalog.Locales.First(l => l.Culture == matched);
            var file = new Dictionary<string, object>
            {
                ["_meta"] = new { name = info.Name, author = info.Author ?? "", baseVersion = typeof(LocaleEndpoints).Assembly.GetName().Version?.ToString(3) }
            };
            foreach (var key in catalog.EnglishEntries.Keys.Order(StringComparer.Ordinal)) file[key] = catalog.Find(matched, key) ?? "";
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(file, IndentedUnescaped), "application/json", $"{matched}.json");
        }).RequireAuthorization("Settings.Edit");

        app.MapPost("/admin/locales/upload", async ([FromForm] string? culture, IFormFile? file, HttpContext ctx,
            LocaleCatalog catalog, IAuditService audit, CancellationToken ct) =>
        {
            if (file is null || file.Length == 0) return Back("upload-empty");
            if (file.Length > LocaleFile.MaxBytes) return Back("upload-too-large");
            var name = string.IsNullOrWhiteSpace(culture) ? Path.GetFileNameWithoutExtension(file.FileName) : culture.Trim();

            string json;
            using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8))
                json = await reader.ReadToEndAsync(ct);

            string canonical;
            try
            {
                catalog.Validate(name, json);
                canonical = LocaleFile.CanonicalName(name);
            }
            catch (FormatException ex)
            {
                return Back("upload-invalid", ("error", ex.Message));
            }

            if (catalog.DropInFolder is not { } folder) return Back("upload-failed");
            try
            {
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, $"{canonical}.json");
                // Written next to the target and moved into place, so the folder watcher never
                // reads a half-written file.
                var temp = path + ".tmp";
                await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), ct);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Back("upload-failed", ("error", ex.Message));
            }
            catalog.Reload();

            await audit.LogAsync("Locale.Upload", ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, ctx.User.Identity?.Name,
                ctx.Connection.RemoteIpAddress?.ToString(), canonical, ct);
            return Back("uploaded", ("culture", canonical));
        }).RequireAuthorization("Settings.Edit");

        app.MapPost("/admin/locales/delete", async ([FromForm] string culture, HttpContext ctx,
            LocaleCatalog catalog, IAuditService audit, CancellationToken ct) =>
        {
            if (!LocaleFile.IsValidCultureName(culture) || catalog.DropInFolder is not { } folder) return Back("delete-failed");
            var canonical = LocaleFile.CanonicalName(culture);
            var path = Path.Combine(folder, $"{canonical}.json");
            if (!File.Exists(path)) return Back("delete-failed");
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Back("delete-failed", ("error", ex.Message));
            }
            catalog.Reload();

            await audit.LogAsync("Locale.Delete", ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, ctx.User.Identity?.Name,
                ctx.Connection.RemoteIpAddress?.ToString(), canonical, ct);
            return Back("deleted", ("culture", canonical));
        }).RequireAuthorization("Settings.Edit");
    }

    private static readonly JsonSerializerOptions IndentedUnescaped = new()
    {
        WriteIndented = true,
        // Translators read and edit this file by hand: keep é and ü as they are, not é.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static IResult Back(string result, params (string Key, string Value)[] extra)
    {
        var query = new StringBuilder($"?result={Uri.EscapeDataString(result)}");
        foreach (var (k, v) in extra) query.Append('&').Append(k).Append('=').Append(Uri.EscapeDataString(v.Length > 300 ? v[..300] : v));
        return Results.Redirect(AdminPage + query);
    }

    private static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
            ? returnUrl
            : "/";
}
