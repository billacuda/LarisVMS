using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Rcordr.Core.Entities;
using Rcordr.Core.Interfaces;

namespace Rcordr.Web.Pages.Admin;

[Authorize("Nodes.View")]
public class NodesModel(INodeService nodeService, ICameraService cameraService) : PageModel
{
    public List<Node> Nodes { get; set; } = [];
    public Dictionary<Guid, int> CameraCountByNode { get; set; } = [];

    public async Task OnGetAsync()
    {
        Nodes = await nodeService.ListAsync();
        var cameras = await cameraService.ListAsync();
        CameraCountByNode = cameras
            .Where(c => c.NodeId is not null)
            .GroupBy(c => c.NodeId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
    }
}
