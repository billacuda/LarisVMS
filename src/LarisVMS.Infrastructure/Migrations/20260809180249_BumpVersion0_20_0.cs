using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_20_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 20, 0, GETUTCDATE(), 'Playback tiles now show a status during the segment-lookup phase instead of staying blank with nothing visible while a fetch is in flight or silently failing. A genuine fetch failure now logs to the console and shows a status message instead of leaving no trace. No LarisVMS.Node changes. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 20 AND Patch = 0");
        }
    }
}
