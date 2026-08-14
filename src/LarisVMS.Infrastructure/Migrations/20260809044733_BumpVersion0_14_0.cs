using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_14_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 14, 0, GETUTCDATE(), 'Fixed Playback defaulting its playhead to right-now, which is almost never covered by an actual recording, so nothing ever loaded and Play did nothing - now resolves the most recent real recording instead. Bootstrap (5.3.8) and GridStack (12.6.0) are now vendored locally instead of loaded from a CDN; CSP tightened to match. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 14 AND Patch = 0");
        }
    }
}
