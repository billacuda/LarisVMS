using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_87_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 87, 1, GETUTCDATE(), 'Fixed: probe-dahua-events.ps1 could not run -- its camera address parameter was named -Host, and $Host is a reserved PowerShell automatic variable, so binding failed immediately with ""Cannot overwrite variable Host because it is read-only or constant."" Renamed to -CameraHost (aliases -Address / -IP). Script only; no application change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 87 AND Patch = 1");
        }
    }
}
