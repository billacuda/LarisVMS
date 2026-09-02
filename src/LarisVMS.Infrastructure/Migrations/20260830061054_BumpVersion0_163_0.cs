using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_163_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 163, 0, GETUTCDATE(), 'Pass 3b of the detection/hardware-acceleration overhaul: opt-in (off by default) High-resolution re-detection setting. When on, the first frame a newly-tracked object starts moving, its pipeline fetches the matching instant from pass 3as Main-stream ring buffer, decodes it once, and runs one batched detection pass over the whole frame plus native-scale tiles placed at the tracks own centroid, merged with a new greedy NMS. Catches small/distant objects a squashed lower-resolution frame misses, and finds a large one whole instead of clipped into tile fragments. This checkpoint stops at logging the merged result rather than writing a snapshot file yet; wiring that in is the next checkpoint (pass 3d), since todays cache file naming keys by a MotionSpan id that does not exist yet at this point in the pipeline.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 163 AND Patch = 0");
        }
    }
}
