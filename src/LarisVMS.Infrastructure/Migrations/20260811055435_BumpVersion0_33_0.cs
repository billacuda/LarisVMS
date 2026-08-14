using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_33_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 33, 0, GETUTCDATE(), 'Live tiles now show native browser video controls on hover instead of an always-visible custom mute button. Fixed a real bug: a Live camera could go blank with no error then resume on its own, because currentTime could drift into an evicted region of the sliding MSE buffer later in a long session, not just at startup - now corrected on every stall where playback is stuck outside all buffered ranges. Investigated but left as-is: the intermittent decode-error-then-recover pattern is expected behavior from the existing slow-client fragment-drop design. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 33 AND Patch = 0");
        }
    }
}
