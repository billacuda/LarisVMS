using LarisVMS.Core.Auth;
using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Middleware;

/// <summary>
/// Sends admin requests to Confirm your password when this session hasn't confirmed it (or signed
/// in) within <see cref="StepUpPolicy.Window"/>; see <see cref="StepUpPolicy"/>. Pages are
/// redirected there and back; API calls are refused. API keys have their own permissions and no
/// session, so they aren't affected. Only while Settings → Security turns it on
/// (<see cref="StepUpPolicy.SettingKey"/>).
/// </summary>
public sealed class StepUpMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, UserSessionService sessions, ISettingsResolver settings)
    {
        if (context.User.Identity?.IsAuthenticated != true
            || context.User.HasClaim(c => c.Type == ApiKeyScope.KeyIdClaim)
            || !StepUpPolicy.IsProtected(context.Request.Method, context.Request.Path)
            || !await settings.GetAsync(StepUpPolicy.SettingKey, false, ct: context.RequestAborted))
        {
            await next(context);
            return;
        }

        var now = DateTime.UtcNow;
        var session = UserSessionService.CurrentRow(context);
        if (session is not null && StepUpPolicy.IsConfirmed(session.ConfirmedUntilUtc, now))
        {
            if (StepUpPolicy.ShouldExtend(session.ConfirmedUntilUtc!.Value, now))
                await sessions.ConfirmAsync(session.Id, now + StepUpPolicy.Window);
            await next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // A form sent after the window closed comes back to its page (unsaved) once confirmed.
        var returnUrl = context.Request.PathBase + context.Request.Path
            + (HttpMethods.IsGet(context.Request.Method) ? context.Request.QueryString.ToString() : "");
#pragma warning disable SCS0027 // a fixed local page; the request's address is only an escaped returnUrl, checked where it's used
        context.Response.Redirect($"{StepUpPolicy.PagePath}?returnUrl={Uri.EscapeDataString(returnUrl)}");
#pragma warning restore SCS0027
    }
}
