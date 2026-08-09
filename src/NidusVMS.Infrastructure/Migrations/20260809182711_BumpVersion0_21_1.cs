using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_21_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 21, 1, GETUTCDATE(), 'Recorded segments could not be decoded by a browser at all, which is why Playback never showed video: the recorder wrote the on-disk segment leg without default_base_moof (only the live pipe leg had it), and MSE cannot accept fragments whose offsets are file-relative - Chrome takes the append and produces no buffered range, with no error raised anywhere. Both tee legs now share one muxer-flag constant, covered by a test. Only newly recorded footage is affected; segments already on disk stay unplayable in the browser but remain valid MP4 for any normal player. THIS IS A NidusVMS.Node CHANGE - re-run install-node.ps1 on every recorder node. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 21 AND Patch = 1");
        }
    }
}
