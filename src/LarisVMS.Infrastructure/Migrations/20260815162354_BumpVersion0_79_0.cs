using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_79_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 79, 0, GETUTCDATE(), 'Added: Dashboard now auto-refreshes every 60s via GET /api/dashboard instead of requiring a manual page reload (IDashboardService.GetHealthAsync shared by both the server-rendered initial load and the AJAX refresh, so the two can never drift apart). New optional thumbnail column (toggle, off by default) shows each camera''s most recent completed segment frame -- a new lightweight lookup, not the heavier live-RTSP snapshot endpoint or the bucketed hover-preview one. All columns are sortable; pagination added with a 10/20/50/100/all rows-per-page choice. Sort and page selection persist across each 60s refresh instead of resetting. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 79 AND Patch = 0");
        }
    }
}
