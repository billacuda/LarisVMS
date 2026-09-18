using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_205_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 205, 0, GETUTCDATE(), 'New Settings hub (Admin -> Settings) replacing the old navbar''s Admin dropdown plus the top-level Nodes/Logs nav buttons. Reworked the web UI''s visual theme: the top navbar is now a sidebar + topbar shell, with colors/typography/zero-radius styling layered over the existing Bootstrap 5.3 install via CSS variable overrides. install-web.ps1 now falls back to -LegacyIisConfigPath and warns loudly instead of silently skipping migrations and node-build/proxy-build registration. Fixed: sidebar not following the light/dark toggle when a Branding AccentColor was configured, .btn-primary hover/active states inverting in dark mode, missing --bs-*-rgb companion variables, cameras list capability badges ignoring the theme, no dark mode support in the Setup wizard, and fullscreen kiosk mode no longer hiding the app shell after the navbar rework.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 205 AND Patch = 0");
        }
    }
}
