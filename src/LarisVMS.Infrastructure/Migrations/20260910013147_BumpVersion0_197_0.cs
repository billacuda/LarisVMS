using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_197_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 197, 0, GETUTCDATE(), 'Fixed: on Views > Play, a fast-moving object got no live AI-detection box unless the Idle overlay toggle was also on, even though it still produced a snapshot. The detection pipeline pruned each track''s movement history every frame against only the tracks matched to a detection that frame, so a moving object that dropped a single detection frame (motion blur, a brief partial occlusion) had its centroid history wiped and was re-reported Idle - with no live box - until it rebuilt two seconds of samples, which fast movers rarely get. Per-track state (movement history, the stable-label arbiter, the snapshot-dedup set) is now pruned against every track ByteTrack still considers alive, including the ones it is briefly coasting for re-acquisition. Slow movers and parked objects are unaffected.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 197 AND Patch = 0");
        }
    }
}
