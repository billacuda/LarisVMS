using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_41_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 41, 0, GETUTCDATE(), 'Added user-configurable ONVIF event tag rules with an admin editor (Camera Edit > Event tags). A rule matches PullPoint notifications by topic (single toggle topic, or matching start/stop topics) and tags the timeline with a chosen color, independent of built-in motion. Optional Drives recording gates Motion-mode keep/discard the same no-timeout way built-in motion does. Topic fields are populated from this camera''s own observed event history. New MotionSource.CustomTag distinguishes custom-tag spans from built-in ones. Event sessions now restart when a camera''s rule set changes, same as zone changes already did. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 41 AND Patch = 0");
        }
    }
}
