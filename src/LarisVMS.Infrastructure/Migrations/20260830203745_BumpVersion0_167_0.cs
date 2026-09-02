using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_167_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 167, 0, GETUTCDATE(), 'The Grid editor UI for pass 3c-2, completing the checkpoint 0.166.0 shipped the backend for. New Pages/Cameras/MotionGrid (linked from Cameras/Index, alongside Zones and Event tags): live video with a click-to-mask cell grid, a 16/32/64 size selector (changing size clears the mask, with confirmation), and a sensitivity slider matching a zones own. Cells wash amber over threshold, red when masked, no fill when quiet. A Make this the active method control switches Camera.MotionRegionMode; both the Zones and MotionGrid editors now show a banner when they are not the active method. New endpoints: GET/PUT /api/cameras/{id}/motion-region and PUT .../motion-region/grid. This completes the full planned pass 3 checkpoint sequence (3a, 3b, 3d, 3c-1, 3c-2).')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 167 AND Patch = 0");
        }
    }
}
