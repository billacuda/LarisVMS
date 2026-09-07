using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_188_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 188, 0, GETUTCDATE(), 'Changed: storage config is now per recorder node - each node has its own recording path and optional archive path (install time or Admin - Nodes); the old global storage-root setting is retired, nodes inheriting it keep their path. Added: archive storage tier - footage that would be deleted by retention is moved to a node secondary volume (SMB/USB) until a separate archive retention; a volume over the watermark archives its oldest footage early. Added: node packages carry a build-number 4th version component so a test rebuild still auto-updates nodes. Added: snapshot badges show a count (Human x2) for several same-type objects; new Detection setting Snapshot motion accuracy. Fixed: no redundant Person under Human in the Snapshots filter tree; a parked vehicle no longer flickers as moving; an object leaving the scene finalizes its own snapshot instead of merging with a later same-type one. Nodes auto-update; only new installs need the updated install-node.ps1.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 188 AND Patch = 0");
        }
    }
}
