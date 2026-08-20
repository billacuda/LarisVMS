using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_114_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 114, 0, GETUTCDATE(), 'Fixes two real M16 playback-speed bugs, both confirmed live. (1) 8x quietly reset to 1x after ~60/8s: every segment transition calls videoEl.load(), which resets playbackRate, and nothing re-applied it. createTile''s player now remembers the desired rate (setPlaybackRate) and re-applies it on every future segment load, not just once. (2) 16x/32x dropped to Loading after a few keyframes: the stepped fast-forward loop fired a new seek every fixed 200ms regardless of whether the previous one had finished, so each seek pre-empted (AbortController) the last before it could ever show a frame. The loop now awaits each step''s seek before scheduling the next, self-pacing to real fetch latency instead of piling up. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 114 AND Patch = 0");
        }
    }
}
