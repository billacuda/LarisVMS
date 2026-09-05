using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_186_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 186, 2, GETUTCDATE(), 'Fixed: on a TensorRT node, only the first camera switched to the Slice aspect-fitting mode worked - every other one failed on every frame with a setInputShape error and produced no detections. ONNX Runtime keys its compiled-engine cache on graph and node names only, never on shapes, so two Slice cameras of different resolutions collided on one cached engine and the second loaded the first camera''s. Each graph variant is now named for the capture size and slice count it was built for. Also fixed: the engine build reported the cache as warm whenever any engine existed, and inference failures were logged with a full stack trace on every frame. Added: the vision log now records a Slice camera''s resolved geometry, and its cadence line reports per-slice detection counts and how many boxes were merged across a slice seam.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 186 AND Patch = 2");
        }
    }
}
