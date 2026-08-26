using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_158_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 158, 0, GETUTCDATE(), 'Fixed post-D-FINE follow-up bugs found on real cameras: Vision Service CPU regression (unfiltered sigmoid + slow pixel conversion), snapshot crops missing the detected object (widened crop margin), duplicate/wrong-category snapshots (grace period + best-frame gating), site-wide login broken (missing Identity _ViewImports.cshtml), live-view boxes not tracking zoom/pan, missing live-tile AI badges, and a snapshot needing two clicks to play (autoplay-vs-stall-watchdog race). Renamed DetectionKind.Person to Human to unify with the AI category. Added a real AI Detection settings page (confidence/IoU sliders with live readout, model family/weights, stream role), a per-camera AI indicator on the camera list, and a collapsible category/label filter tree on Snapshots.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 158 AND Patch = 0");
        }
    }
}
