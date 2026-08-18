using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_95_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 95, 0, GETUTCDATE(), 'Changed: Nodes moved out of the Admin dropdown into its own top-level nav button next to Cameras, gated by Nodes.Edit as before. Changed: the navbar is now permission-aware -- every button is hidden unless the signed-in user holds the permission its target page requires, mirroring each page''s own Authorize policy exactly, rather than always showing the full menu and relying on the page to redirect or 403. The Admin dropdown itself only appears when at least one item inside it would. Added: IPermissionService.GetGrantedAsync resolves every permission a user''s roles grant in at most one database round trip regardless of how many are checked afterward, reading role names from the signed-in user''s own claims rather than re-querying them -- what makes the permission-aware navbar affordable on a view that renders on every page. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 95 AND Patch = 0");
        }
    }
}
