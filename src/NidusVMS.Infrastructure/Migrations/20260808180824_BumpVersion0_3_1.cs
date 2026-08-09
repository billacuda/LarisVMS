using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_3_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 3, 1, GETUTCDATE(), 'build-node.ps1/install-node.ps1 implemented for real (self-contained node package, Windows Service install, direct winget.exe resolution bypassing a flaky PowerShell module layer, ffmpeg copied into the node''s own install dir so the service account can reach it); Admin -> Nodes gained edit (name, per-node storage root override) and delete; Cameras/Index shows each camera''s assigned node; saving an existing camera''s settings now returns to the Cameras list.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 3 AND Patch = 1");
        }
    }
}
