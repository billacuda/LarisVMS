using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_187_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 187, 2, GETUTCDATE(), 'Fixed: dragging the recording timeline no longer flashes the video black between positions. The last decoded frame now stays on screen until the frame at the new position loads, so the picture follows the scrubber instead of strobing; black still appears where there is genuinely no recording for that moment. Changed: database queries that load a camera together with its groups and streams now run as split queries, avoiding a slow combined join on cameras that belong to many groups. Nodes auto-update; no install-node.ps1 re-run.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 187 AND Patch = 2");
        }
    }
}
