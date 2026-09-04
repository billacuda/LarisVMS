using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_182_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 182, 0, GETUTCDATE(), 'Fixed: a camera using the D-FINE detection engine on a node with TensorRT enabled produced no detections and no boxes, with no error - D-FINE is a transformer and its activations overflow FP16 under the TensorRT builder, yielding NaN outputs that decode to nothing. D-FINE now ignores TensorRT and runs on plain CUDA by default; the new Vision:DFineTensorRtMode node setting (Off / Fp32 / Fp16) can re-enable it. When a D-FINE frame decodes to zero detections and the raw output is non-finite, the vision log now warns instead of staying silent. YOLOX and its TensorRT path are unchanged. Also fixed a vision-log line that always reported the TensorRT engine cache as cold at the default cache path. Changed: the Cameras page row actions (Zones, Event tags, Schedule, Re-probe) are now emoji buttons with the label kept as a hover tooltip. Nodes update automatically; no install-node.ps1 re-run.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 182 AND Patch = 0");
        }
    }
}
