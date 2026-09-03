using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_176_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 176, 0, GETUTCDATE(), 'Changed: snapshot cards are now framed more prominently in their badge color(s). Every card border is thicker (3px), and a card that groups overlapping detections on one camera (for example a person and a vehicle in the same frame) is framed in a gradient running through each distinct badge color - two blend corner to corner, three or four anchor one per corner - instead of only showing the first. Cards with a single detection type keep a plain solid border, just wider. Web-only change; no recorder node rebuild or reinstall.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 176 AND Patch = 0");
        }
    }
}
