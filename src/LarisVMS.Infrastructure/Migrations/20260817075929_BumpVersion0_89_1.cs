using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_89_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 89, 1, GETUTCDATE(), 'Fixed: a phone held sideways squeezed every camera into a thin horizontal band. Landscape makes a phone wider than the 768px phone breakpoint, so the saved view rendered through the desktop grid with 12 columns compressed into roughly 840px while row height stayed pinned at the editor''s fixed 60px -- every cell came out far taller and narrower than the box its layout was designed in, and object-fit contain letterboxed the video into a strip. A short viewport now scales row height so the whole view fits the window, measured from the grid''s live position so it stays correct in kiosk mode. Fixed: hovering a View cell''s mini-timeline showed no preview thumbnail -- timeline.js only registers hover previews when a getThumbnailUrl option is supplied, and the mini-timeline never passed one. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 89 AND Patch = 1");
        }
    }
}
