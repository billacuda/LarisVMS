using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_93_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 93, 0, GETUTCDATE(), 'Added: a camera''s device service URL is now editable after it is added (Cameras > Edit), not just at creation -- there was previously no path to change it at all, so switching a camera between http and https or following an IP change meant deleting and re-adding it. Saving a real change re-derives Host/port from the new URL and triggers an automatic re-probe. Fixed: the Dahua/Amcrest plugin''s CGI event connection still validated the camera''s TLS certificate, the one camera-facing HTTP client in this app that did, so an HTTPS camera''s ONVIF traffic worked while its event stream silently never connected. Brought in line with every other camera-facing client. Node change -- install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 93 AND Patch = 0");
        }
    }
}
