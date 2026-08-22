using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_141_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 141, 0, GETUTCDATE(), 'Fixed: recording could get permanently stuck after a storage outage, needing a manual node restart. Root-caused against a real 2+ hour outage: ffmpeg''s segment write hit a dropped SMB path, the stall watchdog correctly killed the process, but Process.Kill() only asks Windows to terminate it -- a process stuck on an uninterruptible kernel-mode I/O wait can take far longer than expected to actually exit. RunAsync awaited that exit with no timeout, so the whole session sat frozen there for the rest of the outage: never reconnecting, recording nothing, nothing logged since nothing threw. The wait is now bounded (KillTimeoutSeconds, 15s default) -- past it, the old process is abandoned and the session moves straight to its existing backoff-and-reconnect cycle. Recording now retries indefinitely instead of ever settling into a stuck state; a node restart is no longer the only way to recover. Node change; install-node.ps1 re-run not needed.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 141 AND Patch = 0");
        }
    }
}
