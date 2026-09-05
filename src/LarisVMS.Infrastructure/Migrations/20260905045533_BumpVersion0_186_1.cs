using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_186_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 186, 1, GETUTCDATE(), 'Fixed: clicking Play on Playback before a camera''s stream finished loading no longer left it silently paused. The page would flip its Play/Pause button and internal state to playing, but the still-loading tile''s own eventual startup checked the autoplay flag captured back when that load began (always false for a fresh page load) instead of the button''s current state, so nothing actually started until the user clicked Pause then Play again to notice. Now re-checks live state and starts the stream itself once it finishes loading.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 186 AND Patch = 1");
        }
    }
}
