using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_148_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 148, 1, GETUTCDATE(), 'Fixed: double-clicking a camera fullscreen on Views > Play (live) showed several seconds of black screen instead of switching instantly. Entering fullscreen could cross a mobile layout breakpoint (shortQuery/phoneLandscapeQuery), whose mode-change listener unconditionally re-rendered the whole view, tearing down and restarting every camera live session. Fullscreen toggling no longer triggers that re-render; a genuine mode change happening while fullscreen still applies once it ends. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 148 AND Patch = 1");
        }
    }
}
