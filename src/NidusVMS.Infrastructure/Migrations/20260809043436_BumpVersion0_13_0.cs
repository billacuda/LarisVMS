using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_13_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 13, 0, GETUTCDATE(), 'Fixed the Playback zoom-button-covers-the-tile bug (same Bootstrap .ratio > * issue as the 0.7.0 mute-button fix). Playback is now driven by saved Views instead of an ad-hoc camera picker, matching how Live already works; Live gained a view picker too (redirects to Views/Play). Added a second, merged coverage timeline on Playback showing recording across every camera, not just the selected one. See CHANGELOG for details and remaining known limitations (still unverified in a real browser).')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 13 AND Patch = 0");
        }
    }
}
