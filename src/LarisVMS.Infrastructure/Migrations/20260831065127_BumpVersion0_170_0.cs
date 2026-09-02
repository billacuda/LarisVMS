using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_170_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 170, 0, GETUTCDATE(), 'High-resolution re-detection now does its pixel work on the accelerator, not the CPU - on a multi-camera node this path (full Main-stream keyframe decode + tiling + per-tile inference per new tracked object) was the detection services real CPU cost. MainFrameDecoder decodes to packed nv12 via NVDEC + scale_cuda/hwdownload where CUDA is present (no 19MB host frame, no software 4K decode); tiles are byte-copied from the nv12 buffer and the whole-frame pass is one small CPU letterbox-resize; nv12->RGB->normalize runs in the ONNX head 0.169.0 added. With GPU preprocessing on the engine now holds one InferenceSession per camera not two, fixing 0.169.0s ~2x VRAM / ~1.2GB host RAM. Behaviour unchanged - same tiles, whole-frame pass, NMS, box and eager crop; still opt-in, off by default. New untested-on-GPU bit: scale_cuda on a piped fragment, which fails safe.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 170 AND Patch = 0");
        }
    }
}
