using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_177_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 177, 0, GETUTCDATE(), 'Added: the Snapshots filter tree now remembers its state per user. Collapsing a category, or unchecking a whole category or a single object label, used to be forgotten on the next refresh unless the user also clicked Filter. Those toggles are now saved to the user account as they are made and restored on the next visit on any device, while a URL that already carries a filter (a Filter submit, a shared link, a pagination click) still wins and is left untouched. Clearing the filter also clears the remembered tree state. Web-only change; no recorder node rebuild or reinstall.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 177 AND Patch = 0");
        }
    }
}
