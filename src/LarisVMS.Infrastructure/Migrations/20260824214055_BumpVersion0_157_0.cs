using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_157_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 157, 0, GETUTCDATE(), 'Added native AI object detection (YOLO/ByteTrack) built into LarisVMS, replacing the separate aitest debug process. New LarisVMS.Vision.Service sibling process per node (GPU/ONNX deps isolated from LarisVMS.Node), a second RTSP session per camera for capture, per-node accelerator selection (Auto/Nvidia/Intel/AMD/CPU) with fallback, categorized detected-object colors, independent Moving/Idle live-view overlay toggles (client-side only), a per-camera motion detection source setting, cropped best-frame snapshots with full storage eviction, and ported model-export tooling. Recorder auto-update also keeps Vision Service current. Unverified against real GPU hardware or an actual camera end-to-end. Requires one install-node.ps1 re-run per recorder this release; Node/NodeUpdater bumped to 0.157.0 in lockstep. New DetectedObjectCategory table and Vision-related columns.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 157 AND Patch = 0");
        }
    }
}
