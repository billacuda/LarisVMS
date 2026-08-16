using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_77_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 77, 0, GETUTCDATE(), 'Fixed: Dashboard fps/bitrate/last-report/reconnects stayed blank forever despite active recording. Root cause: this app''s ffmpeg pipeline always uses -f tee (recording + live view share one RTSP session), and tee never reports a real bitrate on its progress line -- confirmed against a real captured run, permanent for the whole session, not just early startup. The parser required a numeric bitrate to match the line at all, so it silently rejected every progress line and fps/HealthReportedAt never populated either, even though ReconnectCount (not regex-derived) reported fine -- exactly matching the reported symptom. Fps now parses independently of bitrate; bitrate is instead derived from each completed segment''s real byte size over its real duration, the actual source of truth for -c copy recording. Node change -- install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 77 AND Patch = 0");
        }
    }
}
