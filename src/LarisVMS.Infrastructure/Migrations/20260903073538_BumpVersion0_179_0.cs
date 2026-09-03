using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_179_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 179, 0, GETUTCDATE(), 'One recorder node package now runs on any hardware: the Vision Service bundles the DirectML and CPU ONNX Runtime backends and picks one at startup for the detected accelerator (CUDA on an NVIDIA GPU with the CUDA Toolkit, else DirectML incl. Intel iGPUs, else CPU), degrading instead of failing every camera. No accelerator flag on build-node.ps1 / deploy.ps1. The 320 MB CUDA provider library downloads from the server on demand rather than shipping in every package. Fixed TensorRT (Vision:EnableTensorRt), which called an ONNX Runtime API 1.23 rejects and dropped the node to CPU on any CUDA-setup failure; it now uses the supported API and falls back to plain CUDA. Update recorder nodes with install-node.ps1 (not auto-update) to get the bundled backends.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 179 AND Patch = 0");
        }
    }
}
