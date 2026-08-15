using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_62_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 62, 0, GETUTCDATE(), 'Fixed: the stale-segment warning could never clear on its own. StorageManager only swept cameras currently in its own config, so a camera reassigned to a different node left its old footage permanently unmanaged on the previous node -- confirmed live as 1,158 real segments each for two cameras sitting untouched on NVR1 since 08-08. Now swept using the camera''s own actual retention (a new NodeConfigResponse.OrphanedCameras list resolves it server-side, scoped to this node), falling back to 30 days only if unresolvable. LarisVMS.Node change -- install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 62 AND Patch = 0");
        }
    }
}
