using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_122_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 122, 0, GETUTCDATE(), 'Fixed: Snapshots pagination silently never advanced past page 1, confirmed live with a URL reading ...page=26 while the same first item kept showing. Root cause: Razor Pages'' own endpoint routing sets a route value literally named page on every request (the relative page path, used to pick which compiled page runs), and the composite model binder checks route values before the query string -- a handler parameter also named page finds that entry first, fails to parse a page path as an int, and silently binds to 0. Renamed the parameter to pageNumber in Snapshots and in Audit Logs (same latent bug, ported from there originally). Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 122 AND Patch = 0");
        }
    }
}
