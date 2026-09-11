using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_200_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 200, 0, GETUTCDATE(), 'Fixed: a per-node override on Admin > Nodes - most visibly Detection.Backend and its external-service URL/model/API key - could be silently cleared back to inherit-the-global-default by saving that node''s row from a stale copy of the page (a browser tab left open, or reopened via back/forward, from before the override was set). Saving that page now checks every field it can edit against the node''s current state first, and rejects the save outright, nothing written, if anything changed underneath since the page was loaded, instead of writing that stale snapshot''s blanks over a value someone (or something) else set in the meantime. A save rejected this way now shows a message asking you to reload and re-apply the edit.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 200 AND Patch = 0");
        }
    }
}
