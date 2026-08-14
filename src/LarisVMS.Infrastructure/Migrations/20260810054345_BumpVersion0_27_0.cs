using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_27_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 27, 0, GETUTCDATE(), 'First real-browser test of 0.26.0 playback confirmed it worked (faster first frame, tiles within a couple seconds instead of 10-30) and surfaced two bugs. Playback: a missing segment (404) previously triggered a burst of repeated identical requests during a scrub drag (13+ in a row observed live) - now remembered for the rest of the page load instead of re-fetched. StorageManager: a pre-existing gap, unrelated to Motion mode, where a failed deletion-report call could permanently orphan a Segments row pointing at an already-deleted file - deletion reports are now retried until they succeed, same as every other node report in this app. Does not clean up rows already orphaned before this fix. THIS IS A LarisVMS.Node CHANGE - re-run install-node.ps1 on every recorder node. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 27 AND Patch = 0");
        }
    }
}
