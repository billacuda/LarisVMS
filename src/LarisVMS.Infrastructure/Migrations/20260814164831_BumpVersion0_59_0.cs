using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_59_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 59, 0, GETUTCDATE(), 'Added: timeline hover thumbnails (M7 pass 2). Hovering the Playback per-camera timeline shows a small preview frame, generated one per 5-minute bucket, capped at 150x150px preserving aspect ratio, compressed for minimal storage. New signed-proxy pair (MediaToken.IssueForThumbnail/TryValidateThumbnail) and /playback-thumbnail route on Web and Node. Thumbnails cached on the node disk and deleted automatically with their source segment -- no separate retention. LarisVMS.Node change -- install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 59 AND Patch = 0");
        }
    }
}
