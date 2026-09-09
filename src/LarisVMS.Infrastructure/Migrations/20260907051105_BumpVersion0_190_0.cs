using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_190_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 190, 0, GETUTCDATE(), 'Security: recorder-node check-ins are now replay-hardened (failover plan phase 5). Each heartbeat carries a single-use rolling nonce - a replayed heartbeat is rejected and audited (Node.AuthReplay). A wedged nonce (a heartbeat reply the node never received) is cleared with the new Reset auth button on Admin - Nodes. Bearer-secret rotation is wired but off by default (set a node SecretRotationDays to arm it); the old secret keeps working until the node first uses the new one, so there is no downtime. One-shot media tokens (playback segment, hover thumbnail, snapshot crop, node restart) now carry a per-use id and cannot be replayed within their short lifetime; older nodes ignore it harmlessly. Live view is unaffected. Playback and thumbnails from a node may 401 for the few seconds it takes that node to auto-update. Nodes auto-update.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 190 AND Patch = 0");
        }
    }
}
