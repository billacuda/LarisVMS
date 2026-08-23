using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_150_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 150, 1, GETUTCDATE(), 'Fixed: Cameras > Groups manage-cameras popup repeated the same site-conflict warning next to every camera - now stated once at the top instead. Fixed: a nested group card only indented its title text, not the card itself - the whole card now indents by depth so it reads as a real nested section. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 150 AND Patch = 1");
        }
    }
}
