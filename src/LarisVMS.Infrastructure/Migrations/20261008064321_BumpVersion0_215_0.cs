using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_215_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 215, 0, GETUTCDATE(), 'Added: a daily check for new LarisVMS releases, with a notice in the top bar for admins (can be turned off). Changed: the sidebar and top bar stay in place while pages scroll, page titles show in the top bar, and ONNX Runtime telemetry is turned off. Fixed: every page requested a web font from Google.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 215 AND Patch = 0");
        }
    }
}
