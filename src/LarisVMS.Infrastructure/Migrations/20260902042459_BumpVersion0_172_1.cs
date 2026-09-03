using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_172_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 172, 1, GETUTCDATE(), 'Fixed: AI object detection could silently stop producing spans, snapshots, and timeline tags for a busy camera - a moving object was tracked live but never recorded - while static objects on the same camera kept working. The fragment-coalescing logic backdated an existing spans start time onto a value another detection row already held, violating a unique index; the error was caught by dropping the row, so any scene with real activity stopped logging AI detections. Coalescing no longer moves a spans start time, only extends its end. Most visible right after 0.172.0, which restarted every detection pipeline at once.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 172 AND Patch = 1");
        }
    }
}
