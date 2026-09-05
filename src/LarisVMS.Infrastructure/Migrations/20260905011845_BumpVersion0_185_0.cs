using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_185_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 185, 0, GETUTCDATE(), 'Internal: D-FINE and YOLOX detection engines can now run a real batched inference pass (one ONNX Runtime Run call over multiple images) instead of one call per image, laying the groundwork for the planned frame-slicing feature. Not enabled by anything yet - every camera still runs a single image per pass, so there is no behavior or performance change on this release. YOLOX needed its pinned ONNX graph rewritten to accept a batch dimension at load time (done in C#, verified bit-identical to running images separately); D-FINE was already batch-capable. Nodes update automatically; no install-node.ps1 re-run.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 185 AND Patch = 0");
        }
    }
}
