using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_142_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 142, 0, GETUTCDATE(), 'Added: Snapshots shows a live N of M ready counter for the current page''s thumbnails. Each card''s image is generated on demand the first time it is requested -- there is no pre-generated backlog behind it the way hover-preview thumbnails have a background backfill service for -- so a fresh page of never-before-viewed instants can take a few real seconds per image while ffmpeg extracts each frame, previously with no visible sign anything was happening. A small badge next to the page title counts down as each thumbnail resolves (loaded or fell back to No thumbnail available -- both count as done), fading out once every visible card is settled. Purely client-side, scoped to the current page''s own cards. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 142 AND Patch = 0");
        }
    }
}
