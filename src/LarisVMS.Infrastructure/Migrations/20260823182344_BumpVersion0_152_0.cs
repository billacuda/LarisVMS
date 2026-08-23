using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_152_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 152, 0, GETUTCDATE(), 'Added: Cameras > Groups cards are now collapsible - each group name toggles its body (member list, Manage Cameras button, or the built-in All Cameras summary), with a member count shown in the header regardless of state. Expanded by default. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 152 AND Patch = 0");
        }
    }
}
