using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_199_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 199, 0, GETUTCDATE(), 'Changed: the Snapshots browser now shows detections only - AI object detections, camera-native object classes (person / vehicle / face / ...), and custom event tags. Plain motion (movement with no object class) is no longer listed there. Motion is unaffected everywhere else: the Playback timeline still draws its motion bands and live view still shows the motion badge, and no motion history is deleted. The Admin > Settings > Events > Snapshots browser section loses its Motion checkbox; the per-class checkboxes still hide those specific detections. Fixed: an AI object detection could be missing from Snapshots if the old Snapshots-browser Motion checkbox had been unticked - AI detections were incorrectly gated by that motion toggle. This migration also removes the retired Snapshots.Enabled.Motion setting row.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 199 AND Patch = 0");
        }
    }
}
