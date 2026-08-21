using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_126_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 126, 0, GETUTCDATE(), 'Fixed: landscape phones stacked every camera on top of each other -- the phone stack reused the view''s mobileTwoColumn setting, a choice made about a portrait phone. On a ~850x400 landscape phone a single column made each cell ~850px wide and therefore ~478px tall at 16:9, taller than the viewport, so every camera filled more than the screen. Landscape now derives its own column count: the fewest columns (largest cells) that still let a row fit the viewport height, capped at 4, with aspect ratios fully intact so nothing is squashed. Changed: Snapshot thumbnails raised to 720 vertical from 480 (1280 on the longer edge) and JPEG quality eased from q8 to q4 -- at 1280px the compression artifacts were themselves part of hard-to-make-out. Hover previews unchanged. Install-node.ps1 re-run needed -- Node/NodeUpdater bumped to 0.126.0.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 126 AND Patch = 0");
        }
    }
}
