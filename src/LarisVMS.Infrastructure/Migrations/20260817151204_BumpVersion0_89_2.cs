using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_89_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 89, 2, GETUTCDATE(), 'Fixed: Playback squeezed its video grid to a sliver on a phone in landscape -- the same symptom 0.89.1 fixed on Views/Play but a different cause. pbLayout is a fixed-height flex column where the toolbar and timeline strip take their natural height and the grid takes what remains; at roughly 390px tall the strip''s two labels, current-time readout, two canvases and a usage hint wrapping to four lines, plus a wrapped toolbar, claimed nearly the whole column. On a short viewport the hint and labels are now hidden and vertical padding tightened, handing the grid back about 100px without touching the grid itself. The Views editor is deliberately unchanged, since GridStack''s fixed cellHeight there defines what a saved layout''s row units mean. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 89 AND Patch = 2");
        }
    }
}
