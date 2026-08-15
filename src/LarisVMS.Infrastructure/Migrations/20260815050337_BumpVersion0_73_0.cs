using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_73_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 73, 0, GETUTCDATE(), 'Added: application log capture on both tiers via a new minimal FileLoggerProvider (shared via Core) -- Web writes daily-rolling files to .\logs\app-*.log, Node to %ProgramData%\LarisVMS\logs\node-*.log. Each tier sweeps its own files past a fixed 14-day window. New System Logs viewer (Admin > System Logs) tails the Web tier''s own log with a date picker and filter; node logs stay on each node''s local disk for now. M7/M8/M11 finish-up plan, pass 6. Node change -- install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 73 AND Patch = 0");
        }
    }
}
