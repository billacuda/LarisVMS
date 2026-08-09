using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Interfaces;

namespace NidusVMS.Web.Pages.Cameras;

[Authorize("Cameras.Edit")]
public class GroupsModel(ICameraGroupService groupService) : PageModel
{
    [BindProperty] public string NewName { get; set; } = string.Empty;
    [BindProperty] public Guid? NewParentId { get; set; }

    public List<CameraGroup> Groups { get; set; } = [];
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        Groups = await groupService.GetTreeAsync();
    }

    public int Depth(CameraGroup group) => group.MaterializedPath.Count(c => c == '/') - 1;

    public async Task<IActionResult> OnPostCreateAsync()
    {
        await groupService.CreateAsync(NewName, NewParentId);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        Groups = await groupService.GetTreeAsync();
        try
        {
            await groupService.DeleteAsync(id);
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }
        return RedirectToPage();
    }
}
