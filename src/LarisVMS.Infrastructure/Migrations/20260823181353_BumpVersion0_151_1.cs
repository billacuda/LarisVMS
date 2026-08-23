using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_151_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 151, 1, GETUTCDATE(), 'Fixed: Admin > Settings > Camera Access group picker showed raw GUIDs (MaterializedPath, built from each ancestor''s own id) instead of readable names. New GroupPathName walks ParentId and joins each ancestor''s real Name instead. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 151 AND Patch = 1");
        }
    }
}
