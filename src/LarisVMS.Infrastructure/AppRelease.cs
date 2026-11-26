using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LarisVMS.Infrastructure;

/// <summary>
/// The release this build is, and its one-line notes, recorded in AppVersions at startup (and by the
/// setup wizard). Bump it with every release, alongside CHANGELOG.md and each project's
/// &lt;Version&gt; — tools\VersionGuard.ps1 refuses a release where they disagree. Until 0.218.0 this
/// was a data-only BumpVersionX_Y_Z migration per release; those stay as history, but a row
/// written from code works the same on every database server instead of needing one migration each.
/// </summary>
public static class AppRelease
{
    public const string Version = "0.223.2";

    public const string Notes = "Fixed: footage is no longer lost when clocks go back, exports no longer overwrite each other or stay on running, saved changes reach nodes right away, nodes catch up after a long outage, and cameras that come and go recover. Changed: Help and the interface are complete in all nine languages, and nodes and the server do less work. Security: camera access is checked every minute for two-way audio, detection boxes, and motion zones, and the media proxy limits connections.";

    /// <summary>Adds this release's AppVersions row unless it's already there. Idempotent.</summary>
    public static async Task EnsureRecordedAsync(ApplicationDbContext db, CancellationToken ct = default)
    {
        var v = System.Version.Parse(Version);
        if (await db.AppVersions.AnyAsync(a => a.Major == v.Major && a.Minor == v.Minor && a.Patch == v.Build, ct))
            return;
        db.AppVersions.Add(new AppVersion
        {
            Major = v.Major,
            Minor = v.Minor,
            Patch = v.Build,
            ReleasedAt = DateTime.UtcNow,
            Notes = Notes
        });
        await db.SaveChangesAsync(ct);
    }
}
