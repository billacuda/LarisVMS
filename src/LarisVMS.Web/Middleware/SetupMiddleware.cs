using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Middleware;

/// <summary>
/// Redirects every request to /Setup until the wizard completes. Caches the "complete" result in a
/// static volatile bool (frcastr's pattern) so the check stops hitting the database once setup is
/// done, rather than querying on every request forever (rsolva's version).
/// </summary>
public class SetupMiddleware(RequestDelegate next, IServiceScopeFactory scopeFactory)
{
    private static volatile bool _setupComplete;
    private const string SetupPendingItem = "LarisVMS.SetupPending";

    private static readonly string[] ExemptPrefixes =
    [
        // /api/nodes is exempt so a node mid-registration (shown its key on the wizard's Node step,
        // before Branding/Review/FinalizeSetupAsync mark setup complete) is never redirected to
        // /Setup instead of getting a real response.
        // /error is exempt so UseExceptionHandler's re-execution of a failed request shows the error
        // page; redirecting it to /Setup turned any exception on a wizard page into a redirect loop.
        "/setup", "/identity", "/_framework", "/favicon", "/health", "/api/nodes", "/error"
    ];

    /// <summary>True while setup hasn't completed, for a request let through to a wizard-exempt
    /// path. Middleware that reads settings from the database (port segmentation, IP allow lists)
    /// skips its check then: there may be no database configured yet, and nothing to enforce.</summary>
    public static bool IsSetupPending(HttpContext context) => context.Items.ContainsKey(SetupPendingItem);

    public async Task InvokeAsync(HttpContext context)
    {
        if (_setupComplete) { await next(context); return; }

        if (await IsSetupCompleteAsync())
        {
            _setupComplete = true;
            await next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        if (ExemptPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            context.Items[SetupPendingItem] = true;
            await next(context);
            return;
        }

        context.Response.Redirect("/Setup");
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
