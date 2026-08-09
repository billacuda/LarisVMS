using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_21_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 21, 0, GETUTCDATE(), 'Playback tiles that showed Loading then went permanently blank are fixed - the MSE append was succeeding and the seek afterwards assumed a zero-based media timeline, landing outside the buffered range with no error raised. Playback now also auto-advances to the next segment (endOfStream was never called, so the ended event could not fire). Timeline no longer draws recorded coverage in the future: the 0.19.0 clamp capped the visible window instead of the playhead, pinning the marker half a span into the past. Time after now is now drawn as unreachable future. No NidusVMS.Node changes. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 21 AND Patch = 0");
        }
    }
}
