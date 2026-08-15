using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_65_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 65, 0, GETUTCDATE(), 'Fixed: hover thumbnails were blocked by the app''s own Content-Security-Policy regardless of the 0.60.0-0.64.0 fixes. Thumbnails are fetched as a blob and shown via URL.createObjectURL on an <img>, but the CSP''s img-src directive only allowed ''self'' data: https: -- no blob: -- so the browser refused every thumbnail image outright, which looks identical to No preview available. media-src already allowed blob: for MSE video; img-src needed the same widening for images. Confirmed via a live browser console CSP violation report. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 65 AND Patch = 0");
        }
    }
}
