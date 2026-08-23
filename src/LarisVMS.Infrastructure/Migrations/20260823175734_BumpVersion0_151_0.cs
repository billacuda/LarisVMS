using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_151_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 151, 0, GETUTCDATE(), 'Added: a built-in All Cameras group every camera belongs to unconditionally, seeded once and backfilled onto existing cameras (CameraGroupSeedService), never renamable/deletable/removable. Exempt from the single-site rule (top-level and on every camera) without changing CameraGroupPolicy itself - SetCameraGroupsAsync strips it from the site check then always re-adds it. Identified by a fixed sentinel Guid, not a new column. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 151 AND Patch = 0");
        }
    }
}
