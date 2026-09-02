using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_167_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 167, 2, GETUTCDATE(), 'Grid editor: drag-select cells instead of one click each - masking a real area on a 64x64 grid did not scale one click at a time. Pressing down on a cell decides the direction for the whole drag from that cells own current state (unmasked -> painting masked, masked -> painting unmasked); every other cell the pointer crosses while the button stays down is set to that same target state, not toggled individually, so re-crossing a cell mid-drag cannot flip it back. A plain click with no movement still works as a single-cell toggle.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 167 AND Patch = 2");
        }
    }
}
