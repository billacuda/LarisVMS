using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_142_4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 142, 4, GETUTCDATE(), 'Security: Export access is now enforced as a sub-permission of Playback per camera (a CameraAccess Export grant only counts where Playback is also held), resolved regardless of how the underlying grants were configured. Dashboard now hides a camera entirely (row and thumbnail) if the viewer lacks View access, with summary counts computed only over what is visible; /playback-thumbnail/.../latest is camera-scoped even when requested directly. /playback-thumbnail/{cameraId} (Snapshots cards, hover-scrub) now enforces the same per-camera Playback check the rest of the playback surface got in 0.142.3 -- this endpoint was listed as fixed then but never actually changed. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 142 AND Patch = 4");
        }
    }
}
