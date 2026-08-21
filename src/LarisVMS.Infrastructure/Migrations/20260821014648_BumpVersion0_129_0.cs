using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_129_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 129, 0, GETUTCDATE(), 'Fixed: a vendor integration kept talking to a camera''s old address after it changed. ReconcileIntegration compared only the integration key, never the base URI, which is derived from the camera''s DeviceServiceUri -- turning HTTPS off on a camera changes it from https to http while the key stays dahua-cgi, so the session kept its dead address and reconnected forever until the node was restarted. CameraIntegrationRecorder now carries the base URI and the reconcile compares both. Found live on two cameras. Added: a per-node Restart button on Admin -> Nodes that restarts the node Windows Service only, never the machine -- reuses the update path via a new --restart-only mode on NodeUpdater, authorized by a node-scoped token binding the action name. Gated on Nodes.Edit and audited. Install-node.ps1 re-run needed -- Node/NodeUpdater bumped to 0.129.0.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 129 AND Patch = 0");
        }
    }
}
