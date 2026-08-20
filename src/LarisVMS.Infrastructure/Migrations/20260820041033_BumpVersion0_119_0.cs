using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_119_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 119, 0, GETUTCDATE(), 'More live-testing fixes, mostly self-inflicted by v0.118.0''s own new code. Fixed: /playback-thumbnail''s new exact param had no default, so every hover-preview call omitting it 400''d -- minimal APIs treat a defaultless primitive as required. Fixed: exact-instant thumbnails routinely 502''d -- Segment.DurationMs is wall-clock EndUtc-StartUtc, not the file''s real length, and the exact offset targets right at that boundary where ffmpeg''s -ss can overshoot; now retries once at offset 0, always safe. Fixed: the Loading status text still flashed on a prefetched segment transition -- removed, bytes already in hand. Fixed: mouse cursor stuck ''grabbing'' after a pinch that started as a drag. Fixed: Snapshots pagination could duplicate/skip rows -- deterministic secondary sort key. Added: per-visit event-type filter on Snapshots (narrows the admin-wide setting). Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 119 AND Patch = 0");
        }
    }
}
