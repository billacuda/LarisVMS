using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_9_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 9, 0, GETUTCDATE(), 'M6 pass 1: Views & layout editor. Save a camera-wall layout (Pages/Views/Editor, GridStack drag/resize, per-cell aspect ratio, live video per tile) and play it back later (Pages/Views/Play) with a derived single/two-column layout on phones and a fullscreen kiosk mode. Click-to-add from the camera palette rather than true drag-in this pass. See CHANGELOG for details and known limitations.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 9 AND Patch = 0");
        }
    }
}
