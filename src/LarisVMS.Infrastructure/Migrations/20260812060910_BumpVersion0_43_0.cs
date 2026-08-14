using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_43_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 43, 0, GETUTCDATE(), 'Fixed ONVIF-event Motion-mode recording never stopping, confirmed via live production data. The built-in classifier''s 2s closing debounce only evaluates elapsed time on a later Observe call, which a continuously-polled source supplies automatically but an event-driven ONVIF feed does not - a camera''s own true/false pairs recurred every 5-120s with nothing else in between, so the falling edge was silently lost and motion stayed reported active for hours. Every hysteresis CameraEventSession builds now closes immediately on the falling edge (endAfter: Zero) - real cameras already debounce their own state, so nothing was actually being absorbed. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 43 AND Patch = 0");
        }
    }
}
