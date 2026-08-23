using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_150_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 150, 2, GETUTCDATE(), 'Fixed: Admin > Settings > Camera Access - choosing a camera group or single camera as a grant scope never showed a picker and always failed to save. The show/hide script compared the scope select against the enum''s numeric cast while the actual option values render as the enum name, so the comparison never matched and the disabled picker never submitted a value. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 150 AND Patch = 2");
        }
    }
}
