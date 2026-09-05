using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_186_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 186, 0, GETUTCDATE(), 'Added: a new Slice aspect-fitting option for AI detection (Admin > Settings > Detection, per-node override on Admin > Nodes). Instead of padding a wide or tall camera into the square detector input with black bars, Slice scales the short edge to the model size and cuts the long edge into 2+ overlapping squares, each detected at full resolution and merged back into one result - meant to catch small or distant objects a letterboxed camera loses to the padding and downscale. Multiplies inference work by the slice count (roughly 2x for a 16:9 camera); lower the max detection frame rate if drop% climbs on the vision log. Requires GPU frame preprocessing - there is no CPU fallback, so choosing Slice forces that setting on regardless of its own toggle. Nodes update automatically; no install-node.ps1 re-run.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 186 AND Patch = 0");
        }
    }
}
