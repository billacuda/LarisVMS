using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_24_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 24, 0, GETUTCDATE(), 'M8 pass 3: corrects a real gap in 0.23.0 - pre-roll never actually worked, because the keep/discard decision was made immediately on segment completion using only motion observed so far, so it could never credit a segment for motion that had not happened yet. Recording.MotionPaddingSeconds is now two separate settings, Recording.MotionPreRollSeconds (default 10) and Recording.MotionPostRollSeconds (default 30), both configurable globally and per camera on Cameras/Edit. A Motion-mode segment''s keep/discard decision is now deferred until PreRoll seconds after it ends, so an event starting shortly after a segment completes can still retroactively claim it as pre-roll. Still unverified against a real camera. THIS IS A NidusVMS.Node CHANGE - re-run install-node.ps1 on every recorder node. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 24 AND Patch = 0");
        }
    }
}
