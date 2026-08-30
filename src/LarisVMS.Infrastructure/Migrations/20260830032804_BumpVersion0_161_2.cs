using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_161_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 161, 2, GETUTCDATE(), 'Fixed the Snapshots page timing out on every load right after 0.161.1 deployed. That release''s footage guard (a correlated Any() overlap test against Segments) was logically correct, but Segment only had a clustered (CameraId, StartUtc) index and the overlap test also needed EndUtc, forcing a large scan of a camera''s whole segment history per MotionSpan row. Added a new (CameraId, EndUtc) index on Segment so the same check is an index seek instead of a scan; the guard query itself is unchanged from 0.161.1.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 161 AND Patch = 2");
        }
    }
}
