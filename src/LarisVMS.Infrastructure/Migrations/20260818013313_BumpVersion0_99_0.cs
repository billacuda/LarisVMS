using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_99_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 99, 0, GETUTCDATE(), 'Added: user preferences now persist in the database, not just localStorage. Theme, the last-watched Live view, the dashboard thumbnail toggle, every table''s remembered page size, and Playback''s remembered position and 24-hour-clock toggle all follow the signed-in user across devices and logins now, through a new UserPreference table and a small preferences API. Theme still reads localStorage first for its before-first-paint anti-flash apply and only adopts the server value on a device that has never stored a theme choice of its own. The last-watched Live view redirect moved fully server-side. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 99 AND Patch = 0");
        }
    }
}
