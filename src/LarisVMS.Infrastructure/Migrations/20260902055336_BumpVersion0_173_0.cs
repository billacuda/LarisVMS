using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_173_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 173, 0, GETUTCDATE(), 'Added: a medium D-FINE model (Obj2Coco medium) in the detection model selection, alongside the existing small Obj2Coco / Obj365 options (Admin > Settings > Detection, and per-node on Admin > Nodes). Same 80-class COCO vocabulary as small Obj2Coco with a larger backbone - more accurate, more work per frame, so pair it with a capable GPU and a lower detection frame-rate cap. The model is not bundled by default; a node package built with it present picks it up automatically.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 173 AND Patch = 0");
        }
    }
}
