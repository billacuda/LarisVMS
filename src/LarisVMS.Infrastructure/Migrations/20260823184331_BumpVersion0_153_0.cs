using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_153_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 153, 0, GETUTCDATE(), 'Added: field-level help text now collapses behind an info icon (hover or click) across Cameras/Edit and most Admin Settings pages, instead of always sitting under the field - actionable warnings stay visible. Changed: Cameras/Edit no longer edits group membership, just shows current groups with a link to Cameras/Groups, which already owns that. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 153 AND Patch = 0");
        }
    }
}
