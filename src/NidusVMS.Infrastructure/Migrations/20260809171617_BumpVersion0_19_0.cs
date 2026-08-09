using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_19_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 19, 0, GETUTCDATE(), 'The reported timeline in the future turned out not to be a data/timezone bug - the default 24h view is centered on the playhead, so starting at now naturally shows 12h of future on the right half. Timelines now clamp so they can never scroll or zoom past the real current instant. Added: remembered scrub position/zoom between visits, and a 24-hour clock toggle (finished this time - the UI control was missing last release). No NidusVMS.Node changes. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 19 AND Patch = 0");
        }
    }
}
