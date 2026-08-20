using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_125_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 125, 0, GETUTCDATE(), 'Fixed: landscape phones still squished some cameras -- always the same ones. Not a layout-geometry bug like the earlier landscape reports: the derived phone stack sizes each cell from the cell''s stored aspect ratio, and the view editor defaulted every newly-added cell to 16:9 regardless of the camera. Any non-16:9 camera got a cell shaped wrong for it from the moment it was added, and object-fit:contain letterboxed the video into a band inside it. The phone stack now sizes cells from the camera''s real probed resolution (nearest() maps width x height to the closest listed ratio), and the editor defaults a new cell to the camera''s own ratio. Desktop still replays the saved geometry untouched. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 125 AND Patch = 0");
        }
    }
}
