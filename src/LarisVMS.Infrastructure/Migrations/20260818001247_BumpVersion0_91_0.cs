using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_91_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 91, 0, GETUTCDATE(), 'Added: a Plugins page (Admin > Plugins) listing every camera integration this build ships, each with its own version and the cameras actually using it, plus a warning for cameras whose stored integration key matches no provider in this build -- a case that was previously silent. Added: each integration provider now carries its own version starting at 1.0.0, bumped when that provider''s behavior changes, deliberately independent of the application version. Added: event tag position is configurable at Admin > Settings > Live view -- motion and object-detection badges can sit in any corner of a live tile, defaulting to top left, and badges placed in a bottom corner are lifted clear of the hover controls and the playback mini timeline that already live there. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 91 AND Patch = 0");
        }
    }
}
