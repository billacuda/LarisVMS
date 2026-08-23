using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_150_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 150, 0, GETUTCDATE(), 'Fixed: recording gaps where footage was written to disk but never reported, showing on Playback as No recording here. An HttpClient timeout throws TaskCanceledException, which derives from OperationCanceledException - every node-to-server report catch excluded it, so one timed-out request faulted the whole report loop, unnoticed and unlogged, losing the in-flight batch. Confirmed on nvr1: reporting dead 2h20m while recording continued fine. Also fixed: a brief SMB outage could delete rows for footage that still exists, since File.Exists returns false (not an error) on an unreachable share - now guarded by a read/write probe, a re-check pass, and a plausibility ceiling. Added: recovery import that offers back on-disk footage with no row, so existing gaps self-heal. NODE CHANGE - re-run install-node.ps1 on every node.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 150 AND Patch = 0");
        }
    }
}
