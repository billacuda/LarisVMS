using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_86_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 86, 0, GETUTCDATE(), 'Fixed: the Playback page''s timelines disappeared while a tile was fullscreened, leaving no way to scrub the footage being watched. They are page-level elements outside the fullscreened tile, so the browser genuinely does not render them in that state; the real element is now moved into the tile while fullscreen is active and moved back on exit, rather than a second copy being rendered and kept in sync. Added: probe-metadata-track.ps1, a read-only research script answering whether a camera exposes an ONVIF metadata track ffmpeg can demux -- the open question gating bounding-box overlays. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 86 AND Patch = 0");
        }
    }
}
