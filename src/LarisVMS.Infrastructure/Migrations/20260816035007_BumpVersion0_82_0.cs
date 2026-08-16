using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_82_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 82, 0, GETUTCDATE(), 'Added: comprehensive audit logging, every entry with the acting user''s IP. New: Camera.View (per live stream opened), View.Watch and Playback.View (each naming the view and every camera in it), Export.Download (previously untracked -- the most compliance-sensitive step in the export flow), and NodeBuild.Approve/Reject (a fleet-wide action with no coverage before). Changed: every settings/entity change now records what actually changed as old -> new values instead of just that a save happened -- global settings, camera edits (including the retention override that used to ride along invisibly), node edits, and backup settings. Secrets are never written: camera credentials and the node registration key log only that they changed. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 82 AND Patch = 0");
        }
    }
}
