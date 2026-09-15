using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_202_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 202, 0, GETUTCDATE(), 'Fixed Custom (descriptor-driven) ONNX models being unusable on cameras configured for the Slice aspect mode, which previously failed every such camera outright. Fixed duplicate and split detections on Slice-mode cameras when two tiles disagreed on an object''s class (common for visually similar classes) - detections are now reunited across tiles by geometry alone rather than requiring an exact class match first. Slice-mode Custom models can now also automatically use a faster, single-pass detection path when the model itself supports it, with an automatic and transparent fallback to the safe per-tile path otherwise.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 202 AND Patch = 0");
        }
    }
}
