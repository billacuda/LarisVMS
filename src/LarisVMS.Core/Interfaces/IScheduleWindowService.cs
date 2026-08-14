using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Interfaces;

/// <summary>CRUD for ScheduleWindow — same shape as IZoneService, one editor page per camera.</summary>
public interface IScheduleWindowService
{
    Task<List<ScheduleWindow>> ListAsync(Guid cameraId, CancellationToken ct = default);
    Task<ScheduleWindow?> GetAsync(Guid id, CancellationToken ct = default);

    Task<ScheduleWindow> CreateAsync(Guid cameraId, DayOfWeekFlags days, TimeOnly startTime, TimeOnly endTime, CancellationToken ct = default);

    Task UpdateAsync(Guid id, DayOfWeekFlags days, TimeOnly startTime, TimeOnly endTime, bool isEnabled, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
