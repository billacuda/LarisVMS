using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_173_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 173, 1, GETUTCDATE(), 'Security: camera RTSP credentials are no longer written to log files. ffmpeg echoes the full stream URL (username and password included) into its own output, and the recorder logged those lines verbatim - every line at Debug, and any authorization-failed / 401 line at Warning. All ffmpeg output is now scrubbed of embedded user:password before it is logged, across the recording, motion, live, and AI-detection stream sessions. Existing log files may still contain credentials and should be rotated.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 173 AND Patch = 1");
        }
    }
}
