using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_195_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 195, 0, GETUTCDATE(), 'Changed: detection-box jitter rejection is now opt-in, per camera, and tunable. The wobble-rejection half of Admin - Settings - Detection - Snapshot motion accuracy is now its own setting, Detection.RejectMotionJitter, off by default (restoring the pre-0.188 movement classifier), resolvable globally and per camera; its pixel sensitivity is Detection.MotionJitterPixels (1-15, default 3). Detection.SnapshotMotionAccuracy now governs only the prompt snapshot finalization when an object leaves frame, whose grace window is the new Detection.DepartureGraceSeconds setting (1-10, default 5, was a hard-coded 5s). Fixed: a recorder node primary drive could fill to 100% when its archive volume was unreachable - the watermark backstop now always deletes to free real space rather than trying to archive.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 195 AND Patch = 0");
        }
    }
}
