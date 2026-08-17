using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_87_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 87, 0, GETUTCDATE(), 'Added: camera integration plugins, for everything ONVIF cannot express. A provider declares which makes/models it handles; probing matches each camera automatically from the make and model it already reports, and the recorder node starts that vendor session alongside its ONVIF one. Providers are compiled in and listed in one registry rather than loaded from external assemblies -- nodes ship as a single self-contained auto-updating executable, so a drop-in plugin folder would need its own distribution and version-matching channel. First provider: Dahua/Amcrest smart events, reading person and vehicle detections from the camera CGI event API. These cameras classify objects onboard but never publish that over ONVIF, so this is what makes the 0.85.0 detection badges work on them. Node change -- install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 87 AND Patch = 0");
        }
    }
}
