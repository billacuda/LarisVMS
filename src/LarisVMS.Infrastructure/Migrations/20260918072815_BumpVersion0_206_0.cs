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
                VALUES (0, 206, 0, GETUTCDATE(), 'Blazor migration Phase 2 complete (Cameras/Index, Cameras/Groups, Logs/AuditLogs, Admin/Settings/Email, Admin/Settings/Detection, Admin/Nodes, Permissions Matrix/Roles/Users), plus two new minimal-API endpoints (audit log export, node local-model list). Fixed: Snapshots/Audit Logs pagination landing on the Dashboard (bare ?query link vs. <base href>); Snapshots'' filters not surviving a refresh or new login; and three compounding bugs that made every Blazor form 400 or error in production — duplicate antiforgery tokens per form, per-row forms sharing one name across a list, and dropdowns resetting to their first option after save (InputSelect needs a live interactive circuit this app never runs).')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 206 AND Patch = 0");
        }
    }
}
