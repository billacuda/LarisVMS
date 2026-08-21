using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_133_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 133, 0, GETUTCDATE(), 'Fixed: every camera''s smart-event connection was reconnecting every 3 minutes, healthy or not. The read timeout assumed the camera sends a keep-alive every ~30s -- confirmed live that it does not on our subscription, which deliberately excludes the motion codes these cameras emit most of the time. Each reconnect loses a few seconds of events, since attach only delivers from the moment it subscribes. The subscription now includes VideoMotionInfo purely as a keep-alive (a bare State ping mapped to no DetectionKind, so it cannot be mistaken for a detection) and the timeout backstop moves from 3 to 15 minutes. Note: these cameras serve attach to ONE subscriber at a time -- a curl session left open silently starves the node, which still gets HTTP 200 but no events. Install-node.ps1 re-run needed -- Node/NodeUpdater bumped to 0.133.0.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 133 AND Patch = 0");
        }
    }
}
