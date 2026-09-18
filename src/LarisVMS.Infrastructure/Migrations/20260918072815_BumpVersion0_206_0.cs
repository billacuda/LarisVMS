using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_206_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 206, 0, GETUTCDATE(), 'Blazor migration Phase 2 complete: Cameras/Index, Cameras/Groups, Logs/AuditLogs, Admin/Settings/Email, Admin/Settings/Detection, Admin/Nodes, and the Permissions area (Matrix/Roles/Users) all moved off Razor Pages, with the audit-log CSV export and a node''s local-model list carved into real minimal-API endpoints. Fixed: Snapshots/Audit Logs pagination links silently landing on the Dashboard (a bare ?query link resolving against the app shell''s <base href> instead of the current page); Snapshots'' filter checkboxes no longer surviving a refresh or new login (remember-filters.js now also hooks Blazor''s enhanced-navigation event, not just the first hard load); and a cluster of three compounding bugs that made every Blazor form 400 or error in production — duplicate antiforgery tokens on every EditForm, per-row forms sharing one name across a list (rejected outright by Blazor), and every dropdown silently resetting to its first option after save because InputSelect needs a live interactive circuit this app never runs.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 206 AND Patch = 0");
        }
    }
}
