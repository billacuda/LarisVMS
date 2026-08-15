using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_68_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 68, 0, GETUTCDATE(), 'Added: an export range spanning a camera reassignment now splits into one export per node involved instead of failing outright, each reusing the same node-side ffmpeg/concat path scoped to its own segments. Output filenames now always include the node name so split parts never collide, and the Exports page shows which node each item came from. Retrying a 0.66.0-era all-or-nothing failure now goes through this split path. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 68 AND Patch = 0");
        }
    }
}
