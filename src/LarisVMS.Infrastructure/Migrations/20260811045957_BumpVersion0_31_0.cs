using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_31_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 31, 0, GETUTCDATE(), 'Added Admin/Settings: one page for every global setting overridable per node/camera (Retention.Days, Storage.WatermarkPercent, Recording.Mode, MotionPreRoll/PostRollSeconds, Storage.RootPath, Node.RegistrationKey). Replaces the old Retention-only page - that URL now redirects here. RegistrationKey and StorageRootPath were previously only visible during initial Setup with no way to view or change them again afterward. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 31 AND Patch = 0");
        }
    }
}
