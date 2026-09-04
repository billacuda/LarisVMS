using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_179_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 179, 1, GETUTCDATE(), 'Fixed: enabling TensorRT (Vision:EnableTensorRt) stopped recording and live view on the node. The detection engine was built inside the Vision Service /start handler, so the first cold TensorRT build - minutes, saturating every core - ran once per camera concurrently and starved the recorder ffmpeg tee, blocking both its segment-file and live-pipe legs silently until the builds finished. Engines are now built off the request path, one at a time process-wide, at below-normal priority during a build. Overlapping /start calls are prevented, the loopback client times out at 15s instead of 100s, and the overlay poll gets a 2s budget. The node reconciles against the watched-camera list, so a camera the service is not watching no longer stays silently unwatched. TensorRT builder bounded to 2 GiB; TensorRtEngineCachePath now optional.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 179 AND Patch = 1");
        }
    }
}
