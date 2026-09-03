using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_175_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 175, 0, GETUTCDATE(), 'Fixed: lowering the AI detection confidence had no effect below 0.6. Object tracking refused to start a new track for anything scoring under a fixed 0.6, independently of Settings > Detection > Confidence, so lowering it to pick up dimmer or more distant objects did nothing - the detections it let through were discarded by a gate further down. Objects already being tracked were unaffected, since tracking holds an established object through weak detections; only newly-appearing ones were lost. The tracker thresholds now follow the configured confidence. A deployment on the 0.5 default is unaffected. The detection cadence log line also now reports detections per frame versus how many survived tracking, the best raw score seen, and the current live boxes.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 175 AND Patch = 0");
        }
    }
}
