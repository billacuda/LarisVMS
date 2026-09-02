using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_164_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 164, 0, GETUTCDATE(), 'Checkpoint 3d: pass 3b high-res re-detection now actually improves the snapshot a viewer sees, not just a log line. No new eager-write cache needed - the detection box was already normalized and persisted per span (MotionSpan.BestBoxX/Y/W/H), and the existing snapshot-image route already crops lazily from the recorded Main-stream segment at request time. The high-res merged result is matched by overlap (not by label, since native-scale re-detection can genuinely disagree with the stabilized label on what something is - confirmed live on a cat) against the track that triggered it, then competes in the same best-frame contest the continuous pass already feeds. Applied from the inference loop thread via a queue, discarding stale results whose track has since disappeared, since the tracker state that owns this is documented single-owner and not thread-safe.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 164 AND Patch = 0");
        }
    }
}
