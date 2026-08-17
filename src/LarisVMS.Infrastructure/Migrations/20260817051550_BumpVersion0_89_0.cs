using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_89_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 89, 0, GETUTCDATE(), 'Added: a per-tile volume slider beside the mute button on every live and playback tile that has audio, independent per tile. The slider shows effective volume -- above zero unmutes, zero mutes, and the mute button restores the last level. Tiles still always start muted and an unmute is never persisted across a page load. Added: audio codec and sample rate on the health dashboard (new Audio column), read from ffmpeg''s own stream summary rather than ONVIF''s advertised AudioEncoderConfiguration, and not blanked when a node stops reporting since it is a property of the stream rather than a live measurement. Fixed: unmuting a live View cell was undone by the next reconnect, which reloads the same video element and resets it to the muted attribute. Node change -- install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 89 AND Patch = 0");
        }
    }
}
