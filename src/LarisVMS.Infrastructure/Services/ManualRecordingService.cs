using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <inheritdoc cref="IManualRecordingService"/>
public class ManualRecordingService(ApplicationDbContext db) : IManualRecordingService
{
    /// <summary>How long a stopped recording is still sent to nodes. Must outlast a segment's discard
    /// grace period and its length, so footage from just before the stop isn't lost.</summary>
    public static readonly TimeSpan EndedRangeWindow = TimeSpan.FromHours(1);

    /// <summary>The end sent to nodes for a recording that's still running. Far enough off to mean
    /// "open", without the overflow risks of DateTime.MaxValue through JSON and time zones.</summary>
    public static readonly DateTime OpenEndUtc = new(9000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task<ManualRecordingChangeResult> StartAsync(IReadOnlyCollection<Guid> cameraIds, string? actor, CancellationToken ct = default)
    {
        var ids = cameraIds.Distinct().ToList();
        var enabled = await db.Cameras.Where(c => ids.Contains(c.Id) && c.IsEnabled).Select(c => c.Id).ToListAsync(ct);
        var alreadyOn = await db.ManualRecordings.Where(r => ids.Contains(r.CameraId) && r.EndUtc == null)
            .Select(r => r.CameraId).Distinct().ToListAsync(ct);

        var changed = enabled.Except(alreadyOn).ToList();
        var now = DateTime.UtcNow;
        foreach (var cameraId in changed)
            db.ManualRecordings.Add(new ManualRecording { CameraId = cameraId, StartUtc = now, StartedBy = actor });
        await db.SaveChangesAsync(ct);

        return new ManualRecordingChangeResult(changed, enabled.Intersect(alreadyOn).ToList(), ids.Except(enabled).ToList());
    }

    public async Task<ManualRecordingChangeResult> StopAsync(IReadOnlyCollection<Guid> cameraIds, string? actor, CancellationToken ct = default)
    {
        var ids = cameraIds.Distinct().ToList();
        var open = await db.ManualRecordings.Where(r => ids.Contains(r.CameraId) && r.EndUtc == null).ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var recording in open)
        {
            recording.EndUtc = now;
            recording.EndedBy = actor;
        }
        await db.SaveChangesAsync(ct);

        var changed = open.Select(r => r.CameraId).Distinct().ToList();
        var known = await db.Cameras.Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
        return new ManualRecordingChangeResult(changed, known.Except(changed).ToList(), ids.Except(known).ToList());
    }

    public async Task<List<CameraRecordingStateDto>> GetStatesAsync(CancellationToken ct = default)
    {
        var active = (await db.ManualRecordings.AsNoTracking().Where(r => r.EndUtc == null)
                .Select(r => new { r.CameraId, r.StartUtc, r.StartedBy }).ToListAsync(ct))
            .GroupBy(r => r.CameraId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.StartUtc).First());

        var freshAfter = DateTime.UtcNow - DashboardService.HealthFreshWindow;
        var fresh = await db.CameraStreams.AsNoTracking()
            .Where(s => s.Role == CameraStreamRole.Main && s.Camera.IsEnabled && s.HealthReportedAt >= freshAfter)
            .Select(s => new { s.CameraId, s.IsKeepingFootage })
            .ToListAsync(ct);

        var states = new Dictionary<Guid, CameraRecordingStateDto>();
        foreach (var stream in fresh)
        {
            active.TryGetValue(stream.CameraId, out var recording);
            if (stream.IsKeepingFootage == true || recording is not null)
                states[stream.CameraId] = new CameraRecordingStateDto(stream.CameraId, true, recording is not null, recording?.StartUtc, recording?.StartedBy);
        }

        // On, but the camera isn't reporting: the recording is still set, though nothing is being saved.
        foreach (var (cameraId, recording) in active)
            states.TryAdd(cameraId, new CameraRecordingStateDto(cameraId, false, true, recording.StartUtc, recording.StartedBy));

        return states.Values.ToList();
    }
}
