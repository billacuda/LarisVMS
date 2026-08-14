using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_21_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 21, 2, GETUTCDATE(), 'Added fix-legacy-segments.ps1 to repair recorded footage written before 0.21.1 so it plays back in a browser - remuxes in place with -c copy (no re-encode), dry-run by default, resolves its own ffmpeg (bundled node copy, PATH, or a direct download - no winget required). Corrected a TimelineService doc comment that still attributed playback failing to a DateTime timezone-binding bug that was disproven in 0.21.0 - the method is unchanged and still correct, just no longer documented against a bug it never caused. deploy.ps1 now builds the node package as part of every web deploy; install-node.ps1 corrected its LocalSystem network-storage warning. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 21 AND Patch = 2");
        }
    }
}
