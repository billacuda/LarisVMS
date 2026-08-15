using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_72_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 72, 0, GETUTCDATE(), 'Added: database backups (Admin > Backups) -- scheduled daily or on-demand BACKUP DATABASE WITH INIT, retention-by-count cleanup, 20-row history. Built for SQL Express installs with no SQL Agent. Ported near-verbatim from rsolva''s BackupService/BackupHostedService. Restore is deliberately not part of this page. M7/M8/M11 finish-up plan, pass 5. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 72 AND Patch = 0");
        }
    }
}
