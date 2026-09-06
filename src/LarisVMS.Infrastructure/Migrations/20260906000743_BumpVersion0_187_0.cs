using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_187_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 187, 0, GETUTCDATE(), 'Added: D-FINE can now run on TensorRT at FP32, from Admin > Settings > Detection (Off / FP32) with a per-node override - the first time D-FINE can use TensorRT at all (a straight FP16 cast overflows its transformer decoder, unchanged since 0.182.0). FP32 gives graph fusion and kernel selection with no precision risk. An FP16 option is present but disabled: it needs a mixed-precision model no current tool produces correctly; the plumbing is wired so it activates once such a model is bundled. The node-scoped Detection.DFineTensorRtMode setting supersedes the machine-local Vision:DFineTensorRtMode (kept as a fallback); Vision:EnableTensorRt and the TensorRT SDK are still required per node. First start after enabling FP32 recompiles the TensorRT engine (minutes per camera resolution). Nodes auto-update; no install-node.ps1 re-run.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 187 AND Patch = 0");
        }
    }
}
