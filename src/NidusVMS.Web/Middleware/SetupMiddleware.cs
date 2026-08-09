using NidusVMS.Core.Interfaces;

namespace NidusVMS.Web.Middleware;

/// <summary>
/// Redirects every request to /Setup until the wizard completes. Caches the "complete" result in a
/// static volatile bool (frcastr's pattern) so the check stops hitting the database once setup is
/// done, rather than querying on every request forever (rsolva's version).
/// </summary>
public class SetupMiddleware(RequestDelegate next, IServiceScopeFactory scopeFactory)
{
    private static volatile bool _setupComplete;

    private static readonly string[] ExemptPrefixes =
    [
        // /api/nodes is exempt so a node mid-registration (shown its key on the wizard's Node step,
        // before Branding/Review/FinalizeSetupAsync mark setup complete) is never redirected to
        // /Setup instead of getting a real response.
        "/setup", "/identity", "/_framework", "/favicon", "/health", "/api/nodes"
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (_setupComplete) { await next(context); return; }

        var path = context.Request.Path.Value ?? string.Empty;

        if (ExemptPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var setup = scope.ServiceProvider.GetRequiredService<ISetupService>();

        if (await setup.IsSetupCompleteAsync())
        {
            _setupComplete = true;
            await next(context);
        }
        else
        {
            context.Response.Redirect("/Setup");
        }
    }
}

public static class SetupMiddlewareExtensions
{
    public static IApplicationBuilder UseSetupRedirect(this IApplicationBuilder app) =>
        app.UseMiddleware<SetupMiddleware>();
}
