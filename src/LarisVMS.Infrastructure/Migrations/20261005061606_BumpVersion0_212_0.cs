using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_212_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 212, 0, GETUTCDATE(), 'Added: per-node dashboard cards (CPU, memory, network, fps, detection counts); drag-to-zoom on live tiles with right-click/button reset; reset all object colors. Changed: AI boxes stay on objects through pauses; Playback Now jumps to the newest footage. Fixed: AI boxes and zone motion now line up with live video; IPv6 nodes; vision GC churn and frame drops; footage outliving retention after a camera or storage path moves; drag-to-zoom getting stuck panning; box labels off-screen or overlapping; badge overlap.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 212 AND Patch = 0");
        }
    }
}
