using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_100_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 100, 0, GETUTCDATE(), 'Added: a per-user column picker. A Columns toggle on Cameras and the Dashboard lets you hide columns you do not care about, saved through the same per-user preference store as theme and table page size. Built as a reusable module (column-picker.js) -- any table opts in with data-column-picker on the table and data-col on whichever th elements should be toggleable, no further script work needed to add it elsewhere. Hiding is done with injected CSS scoped to that table''s id, composing for free with sorting, pagination, and a page''s own AJAX re-render. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 100 AND Patch = 0");
        }
    }
}
