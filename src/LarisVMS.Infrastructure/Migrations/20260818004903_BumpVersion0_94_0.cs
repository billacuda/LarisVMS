using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_94_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 94, 0, GETUTCDATE(), 'Changed: Logs moved out of the Admin dropdown into its own top-level nav button, with Audit Logs and System Logs as two tabs of one page. Changed: Settings is now one page with tabs sorted alphabetically (Backups, Branding, Cameras, Events, Logs, Nodes, Recording, Storage and Retention), replacing five separate Admin dropdown entries; each tab keeps its own permission gate, and Node Builds stays at its own route with a summary link from the Nodes tab. Old routes redirect to their new home. Added: audit log retention (default 0, forever) and a read-only display of the system log path, both on Settings > Logs. Fixed: the Viewer role''s seeded audit-log permission named the wrong resource and never actually granted access; fixed for new setups, an existing Viewer role needs the row corrected by hand. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 94 AND Patch = 0");
        }
    }
}
