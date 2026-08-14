using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_23_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 23, 0, GETUTCDATE(), 'M8 pass 2: motion now actually controls recording. New Recording.Mode (Continuous/Motion) and Recording.MotionPaddingSeconds settings, resolved per camera on Cameras/Edit. A Motion-mode camera keeps recording continuously but the node discards a completed segment immediately (never reported/indexed) if no configured zone had activity within the padding window. Defaults to Continuous - no existing camera changes behavior without an explicit opt-in - and a Motion-mode camera with no zone configured falls back to keeping everything rather than discarding blind. Unverified against a real camera; this decides what gets permanently deleted, so verify on one low-stakes camera first. THIS IS A LarisVMS.Node CHANGE - re-run install-node.ps1 on every recorder node. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 23 AND Patch = 0");
        }
    }
}
