using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_58_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 58, 0, GETUTCDATE(), 'Fixed: double-click-to-fullscreen did not work on a saved view''s own playback page (/Views/Play/{id}) -- view-play.js had its own hand-rolled tile implementation that was never wired up to the shared fullscreen-tile.js module Live and Playback both use, and was missing its <script> include entirely. Now wired the same way as the other two pages.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 58 AND Patch = 0");
        }
    }
}
