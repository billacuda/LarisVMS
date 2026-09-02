using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_163_4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 163, 4, GETUTCDATE(), 'Nothing bounded how many pass 3b high-res re-detection triggers could run at once across cameras - each spawns its own ffmpeg process plus a batched inference call, with every camera draining its own trigger queue independently. A busy moment on several cameras (or one cluttered scene producing many tracks quickly) could pile up concurrent ffmpeg decodes with no throttle - a plausible cause of reported node CPU pegging, and, via tracker ID churn bypassing the per-track label arbiter, of duplicated snapshot events for one real object. Added a single process-wide gate so only one high-res re-detection runs at a time. Also confirmed via debug images that box placement is correct - a moving cat got a tight, correctly-positioned box, just a wrong label, which is normal model behavior, not a pipeline bug.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 163 AND Patch = 4");
        }
    }
}
