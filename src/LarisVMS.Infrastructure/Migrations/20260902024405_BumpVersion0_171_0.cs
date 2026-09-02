using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_171_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 171, 0, GETUTCDATE(), 'YOLOX is now the default detection engine - fully-convolutional, runs on low-power and non-Nvidia GPUs; Auto resolves to YOLOX everywhere, D-FINE stays supported but opt-in. Per-node YOLOX size picker (Nano..X); models fetched from the server on first use, not bundled. New per-node detection frame-rate cap (default 10 fps) stops thermal throttling on larger sizes. AI-detection snapshots are cropped from the exact frame the model detected on, not a timestamp seek, so the object cannot have moved off the crop; overlapping detections on one camera collapse to one multi-badge card; the detection instant is the frames real read time. Optional per-node high-resolution snapshots crop from the native Sub stream. Also: Playback Now button, catch-up badge, Snapshots pagination above the grid, masked registration key, filter-tree carets, quieter logs, ffmpeg no longer bundled.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 171 AND Patch = 0");
        }
    }
}
