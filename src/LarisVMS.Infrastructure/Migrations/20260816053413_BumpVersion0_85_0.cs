using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_85_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 85, 0, GETUTCDATE(), 'Added: object-detection events. Cameras whose onboard analytics report person/vehicle/face over ONVIF now show a labelled badge on their live tile and a distinctly coloured span on the timeline, instead of only generic motion. Reuses the PullPoint channel, MotionSpan pipeline and reporting path already in place -- no new wire format. A detection also counts toward Motion-mode recording, as one more OR term, so it can only keep footage that would otherwise be discarded, never the reverse; that also fixes a camera emitting only object topics and no motion ones, which previously discarded everything under Motion mode. Not bounding boxes: ONVIF rule-engine topics report that a class was seen, not where. Node change -- install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 85 AND Patch = 0");
        }
    }
}
