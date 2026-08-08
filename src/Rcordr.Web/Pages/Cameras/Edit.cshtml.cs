using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Rcordr.Core.Dtos;
using Rcordr.Core.Entities;
using Rcordr.Core.Interfaces;

namespace Rcordr.Web.Pages.Cameras;

[Authorize("Cameras.Edit")]
public class EditModel(ICameraService cameraService, ICameraGroupService groupService, INodeService nodeService) : PageModel
{
    [BindProperty] public Guid? Id { get; set; }
    [BindProperty] public string Name { get; set; } = string.Empty;
    [BindProperty] public string DeviceServiceUri { get; set; } = string.Empty;
    [BindProperty] public Guid? GroupId { get; set; }
    [BindProperty] public Guid? NodeId { get; set; }
    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public bool IsEnabled { get; set; } = true;

    public bool IsNew => Id is null;
    public List<CameraGroup> Groups { get; set; } = [];
    public List<Node> Nodes { get; set; } = [];
    public CameraCapabilities? Capabilities { get; set; }
    public List<CameraStream> Streams { get; set; } = [];
    public string? ErrorMessage { get; set; }
    public string? ProbeMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid? id, string? deviceServiceUri, string? suggestedName)
    {
        Groups = await groupService.GetTreeAsync();
        Nodes = await nodeService.ListAsync();
        Id = id;

        if (id is not null)
        {
            var camera = await cameraService.GetAsync(id.Value);
            if (camera is null) return RedirectToPage("Index");

            Name = camera.Name;
            DeviceServiceUri = camera.DeviceServiceUri;
            GroupId = camera.GroupId;
            NodeId = camera.NodeId;
            IsEnabled = camera.IsEnabled;
            Capabilities = camera.Capabilities;
            Streams = camera.Streams.ToList();
        }
        else
        {
            DeviceServiceUri = deviceServiceUri ?? string.Empty;
            Name = suggestedName ?? string.Empty;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Groups = await groupService.GetTreeAsync();
        Nodes = await nodeService.ListAsync();

        try
        {
            if (Id is null)
            {
                var camera = await cameraService.AddAsync(new AddCameraRequest(Name, DeviceServiceUri, Username, Password, GroupId));
                if (NodeId is not null)
                    await nodeService.AssignCameraAsync(camera.Id, NodeId);
                return RedirectToPage("Edit", new { id = camera.Id });
            }

            await cameraService.UpdateAsync(Id.Value, Name, GroupId, NodeId, Username, Password, IsEnabled);
            return RedirectToPage("Edit", new { id = Id });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostProbeAsync()
    {
        if (Id is null) return RedirectToPage("Index");

        var summary = await cameraService.ProbeAsync(Id.Value);
        ProbeMessage = summary.Error is null
            ? $"Probed successfully — {summary.StreamCount} stream(s) found."
            : $"Probe failed: {summary.Error}";

        var camera = await cameraService.GetAsync(Id.Value);
        if (camera is not null)
        {
            Name = camera.Name;
            DeviceServiceUri = camera.DeviceServiceUri;
            GroupId = camera.GroupId;
            NodeId = camera.NodeId;
            IsEnabled = camera.IsEnabled;
            Capabilities = camera.Capabilities;
            Streams = camera.Streams.ToList();
        }
        Groups = await groupService.GetTreeAsync();
        Nodes = await nodeService.ListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Id is not null) await cameraService.DeleteAsync(Id.Value);
        return RedirectToPage("Index");
    }
}
