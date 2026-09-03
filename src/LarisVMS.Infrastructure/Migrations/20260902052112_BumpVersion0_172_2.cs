using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_172_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 172, 2, GETUTCDATE(), 'Changed: detection span reports from a recorder are now processed one batch at a time per node, and a report that loses a race to another one inserting the same detection span is merged into the winner instead of being logged as an error and dropped. Hardening around the same area as the 0.172.1 fix - no visible behavior change on its own.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 172 AND Patch = 2");
        }
    }
}
