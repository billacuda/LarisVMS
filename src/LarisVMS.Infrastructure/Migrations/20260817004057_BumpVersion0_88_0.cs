using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_88_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 88, 0, GETUTCDATE(), 'Added: event colors are now admin-configurable at Admin > Event Colors -- plain motion, recorded coverage, and each detected object class, with a swatch picker and hex field per entry. Leaving a field blank stores nothing rather than today''s default, so an untouched deployment keeps tracking the built-in palette. Added three more object classes wired through both ONVIF topics and the Dahua/Amcrest integration: Animal, Object appeared (something left behind), and Object missing. Changed: Dahua LeftDetection and TakenAwayDetection now map to those two rather than both collapsing into the generic Object class. Plain motion is marked with a swirl on live tiles. Node change -- install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 88 AND Patch = 0");
        }
    }
}
