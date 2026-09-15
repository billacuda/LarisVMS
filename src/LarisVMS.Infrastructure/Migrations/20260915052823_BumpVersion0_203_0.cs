using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_203_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 203, 0, GETUTCDATE(), 'Fixed incorrect detections on Slice-mode cameras introduced in 0.202.0: objects at a camera''s true image edge could be wrongly absorbed into an unrelated neighboring object, and a fast detection path could silently mislabel where a detection actually was. Cross-tile merging is now gated by a semantic category check, the seam-vs-frame-edge check is tile-aware, and the fast path now numerically verifies itself against a guaranteed-correct fallback before use. Also fixed a pre-existing case where a clipped object could show up as a stray extra box.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 203 AND Patch = 0");
        }
    }
}
