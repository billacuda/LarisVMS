using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_127_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 127, 0, GETUTCDATE(), 'Changed: navbar links are no longer muted. Bootstrap''s navbar-dark renders nav links at rgba(255,255,255,.55) and only brightens to .75 on hover, which reads as every nav item being disabled -- and dims the emoji icons along with the text, since an alpha in color composites the whole glyph against the bar. Links now sit at full white, with hover carried by a slight color shift (Bootstrap''s blue-200) rather than a brightness one, so hover stays legible feedback without the rest state having to be dimmed to make room for it. Open-dropdown and active-link states covered too, which Bootstrap styles separately. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 127 AND Patch = 0");
        }
    }
}
