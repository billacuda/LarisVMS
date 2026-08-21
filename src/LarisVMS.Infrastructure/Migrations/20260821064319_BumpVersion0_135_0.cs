using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_135_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 135, 0, GETUTCDATE(), 'Changed: Snapshot thumbnails now sample from the start of the pre-roll buffer, not the span midpoint -- for every span, not just classified detections. Plain camera-pushed motion can have the same cooldown/anti-dither floor as a detection (some hardware holds it to 10s or more), so the midpoint was never reliable there either. AtUtc is now StartUtc - Recording.MotionPreRollSeconds + 1s (at a 3s pre-roll, 2s before StartUtc) since the subject is typically already visible at the start of what pre-roll actually put on disk, not just at StartUtc. Resolved per camera, upper-clamped to the span end defensively; no lower clamp needed, a candidate before any real segment just shows the existing No thumbnail available placeholder. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 135 AND Patch = 0");
        }
    }
}
