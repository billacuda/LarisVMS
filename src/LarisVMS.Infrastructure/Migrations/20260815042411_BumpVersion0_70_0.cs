using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_70_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 70, 0, GETUTCDATE(), 'Added: audit log viewer (Admin > Audit Log) with filter/pagination, ported from rsolva''s Logs page shape. New audit entries for camera create/update/delete, node update/delete, settings changes, and export creation -- previously only login/logout were logged. M7/M8/M11 finish-up plan, pass 3. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 70 AND Patch = 0");
        }
    }
}
