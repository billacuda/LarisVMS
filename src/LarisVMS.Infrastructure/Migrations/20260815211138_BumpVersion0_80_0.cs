using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_80_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 80, 0, GETUTCDATE(), 'Changed: removed the flat all-cameras Live grid -- saved Views are now the only live-viewing surface. Live always redirects to the last-watched (or first) view, or a create-a-view prompt when none exist; want to see every camera at once, create a View containing all of them. The per-tile playback toggle (scrub the last few minutes of a live tile without leaving the grid) moved to View cells, where it now also works for the first time -- it previously only existed on the flat grid being removed here. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 80 AND Patch = 0");
        }
    }
}
