using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_96_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 96, 0, GETUTCDATE(), 'Added: a new HTTPS column on the Cameras list showing a padlock emoji when a camera''s device service URL uses https, blank otherwise. Read directly from the stored URL rather than a separate flag, since the scheme is already the single source of truth for whether a camera is reached over HTTPS. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 96 AND Patch = 0");
        }
    }
}
