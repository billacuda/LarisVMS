using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_45_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 0.44.0's row is already in the live database describing a cause that turned out to be
            // wrong. Corrected in place rather than left to mislead anyone reading the version
            // history later — same convention CHANGELOG.md follows for superseded claims.
            migrationBuilder.Sql(@"
                UPDATE AppVersions
                SET Notes = 'Fixed untrustworthy ONVIF notification timestamps corrupting the timeline and defeating Motion-mode gating. MotionSpans were stamped with the camera''s reported UtcTime while Segments are stamped by ffmpeg on the node, and on some cameras those disagree by exactly one hour - so motion drew an hour right of its footage, and gating never discarded anything because a future timestamp satisfies any window. Timestamps now anchor to node receive time when the reported value is more than 60s out. NOTE: this release originally blamed drifting camera clocks; that was wrong, see 0.45.0. Also: the merged Playback timeline now only covers cameras in the selected view.'
                WHERE Major = 0 AND Minor = 44 AND Patch = 0");

            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 45, 0, GETUTCDATE(), 'Corrected 0.44.0''s stated cause. The cameras'' clocks are fine - displayed time and NTP sync both verified correct on the hardware. 0.44.0''s per-camera measurement was confounded by stale newest-segment values on cameras with recording gaps. Re-measured using a signal internal to the cameras: a LastClockSynchronization notification carries a timestamp in its payload and one in the message UtcTime attribute, and on four of six cameras those disagree by exactly 60.00 minutes with zero variance across 35+ samples - a daylight-saving conversion bug in the camera ONVIF layer, not drift. The 0.44.0 code fix was already correct and is unchanged; its explanation, logged warning and comments were not. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 45 AND Patch = 0");
            // 0.44.0's Notes deliberately not reverted to the incorrect original text.
        }
    }
}
