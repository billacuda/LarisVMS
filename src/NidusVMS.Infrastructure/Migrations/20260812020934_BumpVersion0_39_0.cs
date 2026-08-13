using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_39_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 39, 0, GETUTCDATE(), 'Live tiles no longer use native video controls - confirmed live that clicking anywhere on the video paused a continuous live stream in most browsers, which made no sense for content with nothing to resume from. Replaced with a minimal custom overlay of exactly two buttons (mute and fullscreen) shown on hover, with no click-on-video behavior bound at all. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 39 AND Patch = 0");
        }
    }
}
