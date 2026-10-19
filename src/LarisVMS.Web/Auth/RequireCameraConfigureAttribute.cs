using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Auth;

/// <summary>
/// On a camera's configuration pages (edit, zones, schedule, speed traps, device settings): besides
/// the page's Cameras.Edit policy, the signed-in user needs Configure access to that camera. The
/// camera comes from the route's <c>{id}</c>, or a posted <c>Id</c>; a page with neither (adding a
/// new camera) only needs the policy.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequireCameraConfigureAttribute : Attribute, IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var http = context.HttpContext;
        Guid? cameraId = null;
        if (Guid.TryParse(context.RouteData.Values["id"]?.ToString(), out var routeId)) cameraId = routeId;
        else if (http.Request.HasFormContentType && Guid.TryParse(http.Request.Form["Id"].ToString(), out var formId)) cameraId = formId;

        if (cameraId is { } id)
        {
            var access = http.RequestServices.GetRequiredService<ICameraAccessService>();
            var configurable = await access.GetAccessibleCameraIdsAsync(http.User, CameraAccessActions.Configure, http.RequestAborted);
            if (configurable is not null && !configurable.Contains(id))
            {
                context.Result = new ForbidResult();
                return;
            }
        }
        await next();
    }
}
