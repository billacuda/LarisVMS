using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_44_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 44, 0, GETUTCDATE(), 'Fixed camera clock skew corrupting the timeline and defeating Motion-mode gating. MotionSpans were stamped with the camera''s own reported time while Segments are stamped by ffmpeg on the node - two clocks in one timeline. Confirmed live that every camera was running 2 to 60 minutes fast and drifting, so motion was drawn up to an hour right of its footage, and gating never discarded anything because a future timestamp satisfies any window. Notification times now anchor to node receive time when the camera is more than 60s off, with a warning naming the skew. Also: the merged Playback timeline now only covers cameras in the selected view. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 44 AND Patch = 0");
        }
    }
}
