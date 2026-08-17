using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_84_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 84, 0, GETUTCDATE(), 'Added: Admin > Branding — set the site name, primary/navbar colors, a logo and a font, applied across every page including the sign-in page (which now uses the app''s own layout rather than the Identity package''s standalone one). Branding moved from the one-time Setup wizard value into ordinary editable settings; the wizard''s value still applies as the fallback until something is saved, so a fresh install looks unchanged. The logo is stored as a data URI in the setting row — no file uploads, no filesystem writes, nothing to orphan on redeploy. Values are allowlist-validated (hex colors, a fixed font table, image-only data URIs, no SVG) since they are emitted into a style block on every page. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 84 AND Patch = 0");
        }
    }
}
