using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_209_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Detection.MaxFps's built-in default dropped from 10 to 7 this release. Keep an existing
            // install (one that already has cameras and never saved its own value) on 10; a fresh
            // install has no cameras yet when migrations run, so it gets the new default.
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM Cameras)
                   AND NOT EXISTS (SELECT 1 FROM Settings WHERE [Key] = 'Detection.MaxFps')
                    INSERT INTO Settings (Id, [Key], [Value], IsSystemSetting, CreatedAt)
                    VALUES (NEWID(), 'Detection.MaxFps', '10', 0, GETUTCDATE());");

            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 209, 0, GETUTCDATE(), 'Added: Help section and a node edit page. Changed: settings, node and camera pages cleaned up into grouped sections with one-line hints; compact Nodes list; alphabetical Settings tiles. Default max detection frame rate is 7 for new installs. Fixed: Dashboard node count, Email provider fields, stale CSS/JS after updates.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 209 AND Patch = 0");
        }
    }
}
