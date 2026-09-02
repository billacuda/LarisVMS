using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_166_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 166, 0, GETUTCDATE(), 'Backend for pass 3c-2 (Grid mode), no editor UI yet. New Camera.MotionRegionMode (Polygon default | Grid), MotionGridSize, MotionGridMask, MotionGridSensitivity - switching modes never touches the inactive methods own config. New MotionGrid (Media) reuses ZoneRasterizers exact mask shape so MotionSession needs no knowledge of which mode built a mask, plus a single-pass per-cell scorer (not one MotionDetector.Score call per cell). NodeWorker.ReconcileMotion branches on mode - Grid feeds one aggregate region, reports ZoneId = null (already supported), a fully-masked grid stops the session, and mode/size/mask/sensitivity are all in the restart signature. The motion-zones WebSocket payload is now wrapped ({zones, cellScores}) so Grid mode per-cell scores can ride the same message. Still to come: the actual Grid editor page - nothing here lets a camera be switched into Grid mode yet.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 166 AND Patch = 0");
        }
    }
}
