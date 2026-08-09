using NidusVMS.Core.Entities;

namespace NidusVMS.Core.Interfaces;

/// <summary>M6 saved camera-wall layouts (Pages/Views/*). Visibility/edit rules live here, not in
/// the page models, so Index/Editor/Play agree on the same "owner or shared" rule.</summary>
public interface IViewService
{
    /// <summary>Views this user can see: their own plus every IsShared view, ordered by name.</summary>
    Task<List<View>> ListVisibleToAsync(string userId, CancellationToken ct = default);

    /// <summary>Null if the view doesn't exist or userId can't see it (not the owner, not shared).</summary>
    Task<View?> GetVisibleToAsync(Guid id, string userId, CancellationToken ct = default);

    Task<View> CreateAsync(string name, string ownerId, CancellationToken ct = default);

    /// <summary>Throws UnauthorizedAccessException unless userId owns the view or the view is
    /// shared (a shared view is editable by anyone with Views.Edit, enforced at the page level;
    /// this only blocks editing someone else's *personal* view).</summary>
    Task UpdateAsync(Guid id, string userId, string name, bool isShared, string layoutJson,
        int sequenceIntervalSeconds, CancellationToken ct = default);

    /// <summary>Same ownership rule as UpdateAsync.</summary>
    Task DeleteAsync(Guid id, string userId, CancellationToken ct = default);

    /// <summary>Shared views with a tour interval set, ordered by name — what "Start tour" on
    /// Views/Index cycles through.</summary>
    Task<List<View>> GetTourViewsAsync(CancellationToken ct = default);
}
