using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_169_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 0.169.0 shipped without its own BumpVersion migration — insert its AppVersions row here
            // too so the footer/version history isn't stuck at 0.168.0.
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM AppVersions WHERE Major = 0 AND Minor = 169 AND Patch = 0)
                    INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                    VALUES (0, 169, 0, GETUTCDATE(), 'GPU frame preprocessing (Admin > Settings > Detection, node-scoped, off by default): the per-frame YUV->RGB conversion and normalization that AI detection did as a CPU pixel loop, plus ffmpegs own YUV->BGRA swscale, now run on the accelerator. A small preprocessing head of standard ONNX ops is merged into the detection model at load, so ONNX Runtime schedules it on the same execution provider as the model - CUDA, DirectML or OpenVINO - with no vendor-specific code. ffmpeg emits packed nv12 straight through and the detection frame flows as raw bytes instead of a decoded bitmap. New permissive deps: Google.Protobuf (BSD-3-Clause), a build-compiled onnx-ml.proto (Apache-2.0), Grpc.Tools (Apache-2.0, build-only).')");

            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 169, 1, GETUTCDATE(), 'High-res re-detection is the dominant CPU cost of the detection service on a multi-camera node (confirmed on real hardware: it decodes the full Main-stream keyframe - 3-4K - in software, tiles it, and CPU-preprocesses the batch, thousands of times an hour). This release moves the Main-stream keyframe decode onto NVDEC (-hwaccel cuda) where the node has CUDA - a software HEVC decode of a 4K frame was most of it. Also quieter logging: the per-trigger detection dump drops to Debug and the HttpClient request play-by-play is filtered to Warning, both of which were flooding the vision log. A deeper GPU rework of the tiling + batch preprocessing is planned separately.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 169 AND Patch = 1");
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 169 AND Patch = 0");
        }
    }
}
