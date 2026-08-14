using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_11_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 11, 0, GETUTCDATE(), 'Pages/Live no longer shows the ""Every camera below connects automatically..."" explainer paragraph. See CHANGELOG for details, plus two deploy/install tooling fixes (deploy.ps1 now builds the node package too; install-node.ps1''s LocalSystem network-storage warning corrected) that did not need an app-version bump.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 11 AND Patch = 0");
        }
    }
}
