using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_38_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 38, 0, GETUTCDATE(), 'Fixed Playback video tiles growing past their space and covering the timeline (a portrait 9:16 camera''s intrinsic aspect ratio could force its grid row taller than available, since CSS Grid items default to min-height:auto) - each tile now gets min-width/min-height:0. Fixed a non-16:9 camera being stretched on Pages/Live (missing object-fit:contain). Fixed the timeline showing thin flickering stripes instead of solid coverage bars, worse during playback - adjacent same-colored buckets are now merged into one fill run instead of drawn separately. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 38 AND Patch = 0");
        }
    }
}
