using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_56_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 56, 0, GETUTCDATE(), 'Renamed NidusVMS to LarisVMS project-wide: namespaces, database name, IIS site/pool, and the recorder node Windows service (NidusVMSNode -> LarisVMSNode) plus its %ProgramData% folder. LarisVMS.Node change -- install-node.ps1 re-run needed on every recorder; the old service must be manually stopped and removed first since the auto-updater matches by the old service name and cannot rename it in place.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 56 AND Patch = 0");
        }
    }
}
