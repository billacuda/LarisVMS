using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_161_6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 161, 6, GETUTCDATE(), 'Fixed the last 2 of 24 Snapshots cards that still 502d after 0.161.5 (confirmed to fail in under a second, a genuine per-span capture failure, not gate contention). Segment.DurationMs is wall-clock derived, not a re-measurement of the files actual encoded duration, and GetSnapshotImageInfoAsync clamps its offset against that same possibly-inflated value, so a best-frame instant near a short segments believed end could still land past the real content. This exact drift was already fixed once for /playback-thumbnails sibling lookup with a retry at offset 0 on a 502; /snapshot-image never got the same fix. Added it. SnapshotImageCapture also now logs ffmpegs own stderr on failure instead of just no bytes.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 161 AND Patch = 6");
        }
    }
}
