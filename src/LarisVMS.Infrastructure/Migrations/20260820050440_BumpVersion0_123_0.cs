using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_123_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 123, 0, GETUTCDATE(), 'Fixed: Snapshots'' Play links (thumbnail and card-footer button) went nowhere -- confirmed live via copy-link-address that the rendered href pointed back at /Snapshots with the page''s own current filter state, not at /Playback with a camera and instant. The asp-page/asp-route-* tag-helper form was the common thread; Bookmarks'' own Play link does the identical job via a plain hand-written href string and has been confirmed working since M18. Switched Snapshots to the same plain-string form. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 123 AND Patch = 0");
        }
    }
}
