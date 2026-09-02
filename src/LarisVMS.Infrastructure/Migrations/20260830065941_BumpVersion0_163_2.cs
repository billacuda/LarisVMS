using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_163_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 163, 2, GETUTCDATE(), 'Fixed the Node process crashing entirely after an ordinary HTTPS timeout talking to the web tier. StorageManager, NodeWorker, and ThumbnailBackfillService caught failures with a filter meant to let real shutdown cancellation through uncaught, but HttpClient.Timeout also throws a TaskCanceledException (which is an OperationCanceledException), so every timeout slipped past the filter uncaught and triggered the hosts default StopHost behavior, tearing down the whole node over a transient connection drop. Fixed at all 7 affected call sites by checking the actual cancellation token state instead of the exceptions static type. Also added a temporary debug image dump for pass 3bs high-res re-detection to confirm reported boxes actually line up with real objects in the scene.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 163 AND Patch = 2");
        }
    }
}
