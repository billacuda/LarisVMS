using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_90_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 90, 0, GETUTCDATE(), 'Fixed: every deploy deleted the entire application log history, the actual reason Admin > System Logs only ever offered the current day. Retention was never the problem -- deploy.ps1 mirrors the site with robocopy /MIR and excluded data-protection-keys, recordings, spool and exports but not logs, so each deploy mirrored the log directory away. logs is now excluded too. Added: application log retention is configurable at Admin > Settings > Logs, replacing the hardcoded 14 days, where 0 keeps logs forever; the value is re-read on every sweep so a change needs no app pool recycle. Recorder nodes still sweep their own logs on a fixed schedule. Added: view pickers on Live and Playback now show each view''s camera count, e.g. House (6). Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 90 AND Patch = 0");
        }
    }
}
