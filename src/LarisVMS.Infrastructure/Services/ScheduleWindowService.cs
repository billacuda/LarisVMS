using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

public class ScheduleWindowService(ApplicationDbContext db) : IScheduleWindowService
{
    public async Task<List<ScheduleWindow>> ListAsync(Guid cameraId, CancellationToken ct = default)
        => await db.ScheduleWindows.AsNoTracking().Where(w => w.CameraId == cameraId)
            .OrderBy(w => w.StartTime).ToListAsync(ct);

    public async Task<ScheduleWindow?> GetAsync(Guid id, CancellationToken ct = default)
        => await db.ScheduleWindows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);

    public async Task<ScheduleWindow> CreateAsync(Guid cameraId, DayOfWeekFlags days, TimeOnly startTime, TimeOnly endTime, CancellationToken ct = default)
    {
        var window = new ScheduleWindow
        {
            Id = Guid.NewGuid(),
            CameraId = cameraId,
            Days = days,
            StartTime = startTime,
            EndTime = endTime
        };
        db.ScheduleWindows.Add(window);
        await db.SaveChangesAsync(ct);
        return window;
    }

    public async Task UpdateAsync(Guid id, DayOfWeekFlags days, TimeOnly startTime, TimeOnly endTime, bool isEnabled, CancellationToken ct = default)
    {
        var window = await db.ScheduleWindows.FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw new InvalidOperationException("Schedule window not found.");
        window.Days = days;
        window.StartTime = startTime;
        window.EndTime = endTime;
        window.IsEnabled = isEnabled;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
        => await db.ScheduleWindows.Where(w => w.Id == id).ExecuteDeleteAsync(ct);
}
