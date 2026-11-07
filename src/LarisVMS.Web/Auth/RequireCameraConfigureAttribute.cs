using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Auth;

/// <summary>
/// On a camera's configuration pages (edit, zones, schedule, speed traps, device settings): besides
/// the page's Cameras.Edit policy, the signed-in user needs Configure access to that camera. The
/// camera comes from the route's <c>{id}</c> and a posted <c>Id</c>; both are checked, since a page's
/// bound <c>Id</c> takes the posted value over the route's. A page with neither (adding a new camera)
/// only needs the policy.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequireCameraConfigureAttribute : Attribute, IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var http = context.HttpContext;
        var cameraIds = new List<Guid>();
        // Every value is checked one by one. Joining them ("Id=X&Id=X" reads as "X,X") would fail to
        // parse and skip the check, while model binding still picks up X. A value that isn't a camera
        // id at all is refused for the same reason.
        var values = new List<string?> { context.RouteData.Values["id"]?.ToString() };
        if (http.Request.HasFormContentType) values.AddRange(http.Request.Form["Id"]);
        values.AddRange(http.Request.Query["id"]);
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            if (!Guid.TryParse(value, out var id))
            {
                context.Result = new ForbidResult();
                return;
            }
            cameraIds.Add(id);
        }

        if (cameraIds.Count > 0)
        {
            var access = http.RequestServices.GetRequiredService<ICameraAccessService>();
            var configurable = await access.GetAccessibleCameraIdsAsync(http.User, CameraAccessActions.Configure, http.RequestAborted);
            if (configurable is not null && cameraIds.Any(id => !configurable.Contains(id)))
            {
                context.Result = new ForbidResult();
                return;
            }
        }
        await next();
    }
}
