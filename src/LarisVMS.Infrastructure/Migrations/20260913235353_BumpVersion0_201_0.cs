using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_201_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 201, 0, GETUTCDATE(), 'Relicensed from MIT to Apache-2.0, matching sibling project SideGlance''s own move from AGPL-3.0, enabling code sharing between the two. Detection is now model-agnostic: a new Custom Detection.ModelFamily runs any ONNX model dropped into C:\ProgramData\LarisVMS\models (never bundled), decoded from its own metadata or a same-basename JSON sidecar, covering YOLO-style and D-FINE-style heads via one mechanism. D-FINE FP16 via TensorRT is enabled, with automatic FP32 rebuild on overflow. The AI-detection backend dropdown now offers CUDA, TensorRT, DirectML, OpenVINO, CPU (fixing a bug where ""Intel"" silently ran DirectML), plus a MIGraphX placeholder. Settings changes reach a running camera pipeline in seconds instead of up to 30. The dashboard shows a spinner/failure badge while a camera''s detection engine builds.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 201 AND Patch = 0");
        }
    }
}
