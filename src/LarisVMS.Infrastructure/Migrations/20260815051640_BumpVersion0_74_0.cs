using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_74_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 74, 0, GETUTCDATE(), 'Added: health dashboard (Dashboard page) showing per-camera real-time fps/bitrate/reconnect-count and each node''s online status, with summary counts. RecordingSession now parses ffmpeg''s periodic progress line for live fps/bitrate and tracks a cumulative reconnect count separate from the existing backoff-timing counter. Reported every ~15s, stored on CameraStream (Fps/BitrateKbps already existed unused; new ReconnectCount/HealthReportedAt). No dropped-frames metric -- this pipeline is -c copy throughout, no decode happens. M7/M8/M11 finish-up plan, pass 7. Node change -- install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 74 AND Patch = 0");
        }
    }
}
