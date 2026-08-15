using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_71_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 71, 0, GETUTCDATE(), 'Fixed: seven admin pages (Exports, Cameras, Nodes, Node Builds, new Audit Log) displayed raw UTC timestamps via ToString(g) with no local conversion, unlike the playback timeline which has always converted correctly via JS. All now use ToLocalTime() before formatting. Added: recorder nodes report their own clock skew every heartbeat (compared against the server''s receive time); Admin > Nodes shows a warning badge past a 60s threshold, the same precedent 0.44.0''s camera DST-bug anchor used. M7/M8/M11 finish-up plan, pass 4. Node change -- install-node.ps1 re-run needed on every recorder (older nodes keep working, just skip the skew measurement until updated).')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 71 AND Patch = 0");
        }
    }
}
