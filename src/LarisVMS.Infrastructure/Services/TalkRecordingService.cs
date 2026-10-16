using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

public class TalkRecordingService(ApplicationDbContext db) : ITalkRecordingService
{
    public async Task AddAsync(TalkRecording recording, CancellationToken ct = default)
    {
        db.TalkRecordings.Add(recording);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<TalkRecordingDto>> ListAsync(Guid cameraId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        fromUtc = TimelineService.NormalizeToUtc(fromUtc);
        toUtc = TimelineService.NormalizeToUtc(toUtc);
        return await db.TalkRecordings.AsNoTracking()
            .Where(t => t.CameraId == cameraId && t.StartUtc < toUtc && t.EndUtc > fromUtc)
            .OrderBy(t => t.StartUtc)
            .Select(t => new TalkRecordingDto(t.Id, t.CameraId, t.StartUtc, t.EndUtc, t.UserName))
            .ToListAsync(ct);
    }

    public async Task<TalkRecording?> GetAsync(long id, CancellationToken ct = default)
        => await db.TalkRecordings.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
}
