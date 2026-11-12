using LarisVMS.Core.Entities;

namespace LarisVMS.Core.Interfaces;

/// <summary>M6 saved camera-wall layouts (Pages/Views/*). Visibility/edit rules live here, not in
/// the page models, so Index/Editor/Play agree on the same "owner or shared" rule.</summary>
public interface IViewService
{
    /// <summary>Views this user can see: their own plus every IsShared view, ordered by name.</summary>
    Task<List<View>> ListVisibleToAsync(string userId, CancellationToken ct = default);

    /// <summary>Null if the view doesn't exist or userId can't see it (not the owner, not shared).</summary>
    Task<View?> GetVisibleToAsync(Guid id, string userId, CancellationToken ct = default);

    Task<View> CreateAsync(string name, string ownerId, CancellationToken ct = default);

    /// <summary>The owner may change their view; someone else's shared view only when
    /// <paramref name="canManageAll"/> (an administrator). Others can use a shared view but not change
    /// it or stop sharing it. Throws UnauthorizedAccessException otherwise.</summary>
    Task UpdateAsync(Guid id, string userId, string name, bool isShared, string layoutJson,
        int sequenceIntervalSeconds, bool canManageAll = false, CancellationToken ct = default);

    /// <summary>The owner may always delete their view; someone else's shared view only when
    /// <paramref name="canManageAll"/> (an administrator). Throws UnauthorizedAccessException otherwise.</summary>
    Task DeleteAsync(Guid id, string userId, bool canManageAll, CancellationToken ct = default);

    /// <summary>Shared views with a tour interval set, ordered by name — what "Start tour" on
    /// Views/Index cycles through.</summary>
    Task<List<View>> GetTourViewsAsync(CancellationToken ct = default);
}
