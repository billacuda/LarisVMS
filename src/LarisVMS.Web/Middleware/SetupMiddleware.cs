using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Middleware;

/// <summary>
/// Redirects every request to /Setup until the wizard completes. Caches the "complete" result in a
/// volatile bool (frcastr's pattern) so the check stops hitting the database once setup is
/// done, rather than querying on every request forever (rsolva's version).
/// </summary>
public class SetupMiddleware(RequestDelegate next, IServiceScopeFactory scopeFactory)
{
    // Instance fields are enough: conventional middleware is constructed once per app.
    private volatile bool _setupComplete;
    private volatile bool _setupFinalized;
    private const string SetupPendingItem = "LarisVMS.SetupPending";

    private static readonly string[] ExemptPrefixes =
    [
        // /api/nodes is exempt so a node mid-registration (shown its key on the wizard's Node step,
        // before Branding/Review/FinalizeSetupAsync mark setup complete) is never redirected to
        // /Setup instead of getting a real response.
        // /error is exempt so UseExceptionHandler's re-execution of a failed request shows the error
        // page; redirecting it to /Setup turned any exception on a wizard page into a redirect loop.
        "/setup", "/identity", "/_framework", "/favicon", "/health", "/api/nodes", "/error",
        // PWA manifest/service worker/icons — the browser fetches these on every page, the wizard's too.
        "/manifest.webmanifest", "/sw.js", "/icons"
    ];

    /// <summary>True while setup hasn't completed, for a request let through to a wizard-exempt
    /// path. Middleware that reads settings from the database (port segmentation, IP allow lists)
    /// skips its check then: there may be no database configured yet, and nothing to enforce.</summary>
    public static bool IsSetupPending(HttpContext context) => context.Items.ContainsKey(SetupPendingItem);

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Once the wizard has finished against a reachable database, its pages are closed for good:
        // re-running Database or Branding on a live site would repoint or overwrite it. Cached like
        // _setupComplete; only /setup requests pay for the check until then.
        if (path.StartsWith("/setup", StringComparison.OrdinalIgnoreCase))
        {
            if (!_setupFinalized && await IsSetupFinalizedAsync()) _setupFinalized = true;
            if (_setupFinalized) { context.Response.Redirect("/"); return; }
        }

        if (_setupComplete) { await next(context); return; }

        if (await IsSetupCompleteAsync())
        {
            _setupComplete = true;
            await next(context);
            return;
        }

        if (ExemptPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            context.Items[SetupPendingItem] = true;
            await next(context);
            return;
        }

        context.Response.Redirect("/Setup");
    }

    private async Task<bool> IsSetupFinalizedAsync()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISetupService>().IsSetupFinalizedAsync();
    }

    private async Task<bool> IsSetupCompleteAsync()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISetupService>().IsSetupCompleteAsync();
    }
}

public static class SetupMiddlewareExtensions
{
    public static IApplicationBuilder UseSetupRedirect(this IApplicationBuilder app) =>
        app.UseMiddleware<SetupMiddleware>();
}
