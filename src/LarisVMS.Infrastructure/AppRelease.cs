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
    public const string Version = "0.221.2";

    public const string Notes = "Added: manual recording, a Manual only mode, recording indicators on live tiles, a wider API with per-key limits, languages, and ignored objects. Changed: keyboard and screen reader support across the app, and stronger contrast. Security: many hardening fixes.";

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
