using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_69_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 69, 0, GETUTCDATE(), 'Changed: live view no longer blanks a tile to Reconnecting... text on a decode-error reconnect. Once a tile has ever shown a frame, a reconnect now freezes that last frame on a canvas overlay (matching the video''s own letterboxing) with a small spinner on top, until the new session has a real frame again. Does not address the underlying fragment-gap cause itself (a deliberate node-side tradeoff protecting the recording pipeline from a slow live-view client). Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 69 AND Patch = 0");
        }
    }
}
