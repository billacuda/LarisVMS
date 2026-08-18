using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_102_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 102, 0, GETUTCDATE(), 'Added: live view and playback can now run on a port of their own, separate from the management interface (Admin > Settings > Security). Once set, /live, /playback-segment, /playback-thumbnail, /export-download and camera snapshots stop responding on the management port and every other route stops responding on the new one. The setting alone does not open a socket -- a matching IIS site binding is needed too, documented in the README. Left blank, nothing changes. This closes out M14 (identity, preferences, access control): per-role session lifetime, server-backed user preferences, a per-user column picker, per-camera access control, and this port separation. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 102 AND Patch = 0");
        }
    }
}
