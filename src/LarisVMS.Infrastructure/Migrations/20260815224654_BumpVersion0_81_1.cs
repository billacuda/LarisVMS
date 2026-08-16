using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_81_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 81, 1, GETUTCDATE(), 'Fixed: 0.81.0''s hard-resync fallback seeked BACKWARD (to buffered.start(), which is behind currentTime whenever currentTime is already inside the buffered range), making drift worse and re-firing forever until a page refresh. Very-far-behind sessions now restart cleanly instead of seeking. Fixed: live-view drift timers could leak and keep seeking a video element after playback mode handed it to a different player -- the trigger for the reported loop; timers now bail if the element is no longer theirs, and can no longer be created after their session already ended. Fixed: a View cell''s mini-timeline was 18px tall, clipping its tick labels (now 30px, matching Playback''s), and had no coverage data wired up, so it drew an empty gray track with no blue recorded/green motion. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 81 AND Patch = 1");
        }
    }
}
