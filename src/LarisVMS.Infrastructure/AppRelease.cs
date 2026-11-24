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
    public const string Version = "0.223.1";

    public const string Notes = "Fixed: cameras with AAC audio play again on nodes with ffmpeg 7.1 (the Docker node and Debian 13), a client endpoint host set on a node reaches the server, and browsers that cannot reach a node use the relay. Added: the node page says why a node is relayed.";

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
