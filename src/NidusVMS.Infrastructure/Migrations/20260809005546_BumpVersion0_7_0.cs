using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_7_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 7, 0, GETUTCDATE(), 'M5 pass 1 completed: live view now confirmed working end-to-end in a real browser (H.264 and HEVC, with audio), connects automatically for every camera on page load (no Watch button), and auto-reconnects on its own after a dropped/corrupted session (e.g. wifi roaming). Fixed five independent bugs found getting there: missing IIS WebSocket feature, no TLS cert for the node''s own connection to the server, wrong codec family selected, undeclared audio track in the SourceBuffer, and a late-joining viewer''s currentTime never landing inside the buffered range. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 7 AND Patch = 0");
        }
    }
}
