using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_51_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 51, 0, GETUTCDATE(), 'Fixed: deploy.ps1''s new node-build registration step ran before EF migrations, so the very first deploy after 0.50.0''s own Status/ApprovedAt/ApprovedBy columns landed tried to insert into columns that did not exist yet on that database, failed silently, and left an orphaned exe on disk with no queue row. Registration now runs after migrations apply. The 0.48.0 build stranded by that ordering bug has been registered by hand and is waiting on Admin -> Node Builds.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 51 AND Patch = 0");
        }
    }
}
