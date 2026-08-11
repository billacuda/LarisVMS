using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_25_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 25, 0, GETUTCDATE(), 'M8 pass 4: fixes why the timeline showed no motion at all - a long-running open span never appeared in MotionSpans until it eventually closed, since MotionSpanCompleted only fires on close. Nodes now checkpoint every open span into MotionSpans roughly every 15s, upserted by (CameraId, ZoneId, StartUtc) so repeated checkpoints extend one row. This is also the likely explanation for recording looking continuous under Motion mode on an active scene - correct behavior, just previously invisible. Added: a red Motion badge on Pages/Live camera tiles, polled from a new /api/cameras/motion-state endpoint, derived from the same MotionSpans data rather than a new node round trip. Still unverified against a real camera. THIS IS A NidusVMS.Node CHANGE - re-run install-node.ps1 on every recorder node. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 25 AND Patch = 0");
        }
    }
}
