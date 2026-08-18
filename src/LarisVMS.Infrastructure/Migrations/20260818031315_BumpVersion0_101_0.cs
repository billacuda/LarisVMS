using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_101_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 101, 0, GETUTCDATE(), 'Added: per-camera access control is now enforced. CameraAccess has existed in the schema since M1 but was never written to or read by anything -- it now gates the camera list, live viewing, Playback, and export creation, with a new admin surface at Admin > Settings > Camera Access granting a role access to every camera, one camera group and its sub-groups, or a single camera. A role with no grants is unrestricted, so every existing deployment is unaffected until an admin adds a grant. Role-only for now; per-user grants are schema-ready and enforced identically but have no admin picker yet. Known scoping boundary: the underlying playback data-proxy endpoints do not yet independently re-check CameraAccess, tracked separately. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 101 AND Patch = 0");
        }
    }
}
