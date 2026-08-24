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
                VALUES (0, 157, 0, GETUTCDATE(), 'Added: native AI object detection (YOLO/ByteTrack) built into LarisVMS itself, replacing the need for the separate aitest debug process. New LarisVMS.Vision.Service sibling process per node (localhost-only control API, zero LarisVMS.Node reference to GPU/ONNX deps), a second RTSP session against each camera''s Sub stream for capture, per-node hardware accelerator selection (Auto/Nvidia/Intel/AMD/CPU) with graceful no-accelerator fallback, an auto-colored DetectedObjectCategory catalog (category drives color, specific label rides alongside, e.g. Vehicle - car), independent Moving/Idle live-view bounding-box toggles (client-side only, never baked into recordings), a per-camera Motion detection source setting narrowing the three generic motion signals to exactly one primary while event tag rules and AI detection stay always-on, cropped best-frame snapshot images in a new separate 720p-capped cache tier with full StorageManager eviction wiring, and tools/export-models/ ported for model export self-containment. Recorder-node auto-update now also keeps an already-installed Vision Service exe current (best-effort, never blocks the primary Node exe update), guarded to only ever touch a node that already has one fully installed - a node''s first Vision Service install still needs one install-node.ps1 run. Unverified against real GPU hardware or an actual camera end-to-end. Node change: install-node.ps1 re-run needed on every recorder, once, for this release only (no node has ever had Vision Service before now); LarisVMS.Node and LarisVMS.NodeUpdater bumped to 0.157.0 in lockstep. New DetectedObjectCategory table, new columns on MotionSpan/Camera/Node, and new NodeBuildVersion Vision* columns.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 157 AND Patch = 0");
        }
    }
}
