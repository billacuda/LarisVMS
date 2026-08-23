using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_148_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 148, 2, GETUTCDATE(), 'Fixed: v0.148.1''s fix for the fullscreen black-screen bug did not actually close it. That fix gated the mode-change listener on document.fullscreenElement alone, but the viewport resize that crosses the mobile layout breakpoints is not guaranteed to land after fullscreenchange sets that flag, so the listener could still fire while it read null and still tore down every camera''s live session. fullscreen-tile.js now tracks a fullscreen request/exit as in-flight from the instant it is issued, before the browser call is even made, closing the race instead of racing it. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 148 AND Patch = 2");
        }
    }
}
