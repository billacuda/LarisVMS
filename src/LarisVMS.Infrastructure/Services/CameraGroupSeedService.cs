using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <inheritdoc cref="ICameraGroupSeedService" />
public class CameraGroupSeedService(ApplicationDbContext db, ILogger<CameraGroupSeedService> logger) : ICameraGroupSeedService
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        var exists = await db.CameraGroups.AnyAsync(g => g.Id == CameraGroup.AllCamerasId, ct);
        if (!exists)
        {
            // Same MaterializedPath shape CameraGroupService.CreateAsync produces for any other
            // top-level group — built from the group's own id so it never needs rewriting.
            db.CameraGroups.Add(new CameraGroup
            {
                Id = CameraGroup.AllCamerasId,
                Name = "All Cameras",
                ParentId = null,
                MaterializedPath = $"/{CameraGroup.AllCamerasId}/"
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded the built-in \"All Cameras\" camera group.");
        }

        // Backfill: every camera not yet linked, in one query — a plain approach is enough at this
        // app's realistic camera counts (dozens, not millions), same bar RecordSegmentsAsync's own
        // comment sets elsewhere. A camera created after this seeder already gets the link directly
        // from CameraService.AddAsync, so this only ever catches up rows from before the group existed.
        var alreadyLinkedIds = await db.CameraGroups
            .Where(g => g.Id == CameraGroup.AllCamerasId)
            .SelectMany(g => g.Cameras.Select(c => c.Id))
            .ToListAsync(ct);

        var unlinked = await db.Cameras
            .Where(c => !alreadyLinkedIds.Contains(c.Id))
            .ToListAsync(ct);
        if (unlinked.Count == 0) return;

        var allCamerasGroup = await db.CameraGroups.FirstAsync(g => g.Id == CameraGroup.AllCamerasId, ct);
        foreach (var camera in unlinked) allCamerasGroup.Cameras.Add(camera);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Added {Count} existing camera(s) to the built-in \"All Cameras\" group.", unlinked.Count);
    }
}
