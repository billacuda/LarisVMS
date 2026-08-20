using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_121_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 121, 0, GETUTCDATE(), 'Fixed: either Playback timeline (per-camera or all-cameras) could get stuck mid-drag, unpredictably -- setPointerCapture can silently fail to take, delivering the release event to whatever is under the pointer instead of back to the canvas that started the drag. Added a window-level pointerup/pointercancel fallback that always sees the release. Fixed: a new bookmark did not appear on the timeline until something unrelated triggered a reload -- submitBookmark now reloads the timeline right after a successful save. Added: Snapshot cards show the event duration (EndUtc-StartUtc), not just its timestamp, formatted as 12s / 3m 05s / 1h 02m. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 121 AND Patch = 0");
        }
    }
}
