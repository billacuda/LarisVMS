using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_49_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 49, 0, GETUTCDATE(), 'Fixed: the Live page''s view picker had no counterpart on Views/Play, so picking a saved view left you with no way to switch to another view without clicking Back to views and picking again. Views/Play''s toolbar now carries the same picker, pre-selected to the current view.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 49 AND Patch = 0");
        }
    }
}
