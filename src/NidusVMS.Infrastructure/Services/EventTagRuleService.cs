using Microsoft.EntityFrameworkCore;
using NidusVMS.Core.Dtos;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Interfaces;
using NidusVMS.Infrastructure.Data;

namespace NidusVMS.Infrastructure.Services;

public class EventTagRuleService(ApplicationDbContext db) : IEventTagRuleService
{
    public async Task<List<EventTagRule>> ListAsync(Guid cameraId, CancellationToken ct = default)
        => await db.EventTagRules.AsNoTracking().Where(r => r.CameraId == cameraId).OrderBy(r => r.Name).ToListAsync(ct);

    public async Task<EventTagRule?> GetAsync(Guid id, CancellationToken ct = default)
        => await db.EventTagRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<EventTagRule> CreateAsync(Guid cameraId, string name, string startTopic, string? stopTopic,
        string colorHex, bool drivesRecording, CancellationToken ct = default)
    {
        var rule = new EventTagRule
        {
            Id = Guid.NewGuid(),
            CameraId = cameraId,
            Name = name,
            StartTopic = startTopic,
            StopTopic = string.IsNullOrWhiteSpace(stopTopic) ? null : stopTopic,
            ColorHex = colorHex,
            DrivesRecording = drivesRecording,
            IsEnabled = true
        };
        db.EventTagRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task UpdateAsync(Guid id, string name, string startTopic, string? stopTopic,
        string colorHex, bool drivesRecording, bool isEnabled, CancellationToken ct = default)
    {
        var rule = await db.EventTagRules.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new InvalidOperationException("Event tag rule not found.");
        rule.Name = name;
        rule.StartTopic = startTopic;
        rule.StopTopic = string.IsNullOrWhiteSpace(stopTopic) ? null : stopTopic;
        rule.ColorHex = colorHex;
        rule.DrivesRecording = drivesRecording;
        rule.IsEnabled = isEnabled;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // Same reasoning as ZoneService.DeleteAsync — MotionSpans.EventTagRuleId -> EventTagRules is
        // Restrict at the DB level, so the null-out that SetNull would normally do happens here
        // instead: deleting a rule keeps the tag history it already recorded, it just stops being
        // attributed to a specific rule.
        await db.MotionSpans.Where(m => m.EventTagRuleId == id).ExecuteUpdateAsync(u => u.SetProperty(m => m.EventTagRuleId, (Guid?)null), ct);
        await db.EventTagRules.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<List<ObservedTopicDto>> ListObservedTopicsAsync(Guid cameraId, CancellationToken ct = default)
    {
        var grouped = await db.CameraEvents
            .Where(e => e.CameraId == cameraId)
            .GroupBy(e => e.OnvifTopic)
            .Select(g => new { Topic = g.Key, Count = g.Count(), FirstSeenUtc = g.Min(e => e.ReceivedUtc), LastSeenUtc = g.Max(e => e.ReceivedUtc) })
            .OrderByDescending(x => x.LastSeenUtc)
            .ToListAsync(ct);

        // One extra query per distinct topic for a representative sample payload — a camera
        // realistically has a handful of distinct topics at most, so this is simpler and clearer
        // than contorting one query into "the payload of each group's most recent row".
        var result = new List<ObservedTopicDto>();
        foreach (var g in grouped)
        {
            var sample = await db.CameraEvents
                .Where(e => e.CameraId == cameraId && e.OnvifTopic == g.Topic)
                .OrderByDescending(e => e.ReceivedUtc)
                .Select(e => e.PayloadJson)
                .FirstOrDefaultAsync(ct);
            result.Add(new ObservedTopicDto(g.Topic, g.Count, g.FirstSeenUtc, g.LastSeenUtc, sample));
        }
        return result;
    }
}
