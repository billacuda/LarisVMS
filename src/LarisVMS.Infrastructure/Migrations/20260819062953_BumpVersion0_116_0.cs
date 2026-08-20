using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_116_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 116, 0, GETUTCDATE(), 'M18: bookmarks. New Bookmark entity (camera + instant + note, no FK to Camera, matches ExportJobItem), shared across everyone with Playback.View. Playback toolbar gets a Bookmark button marking the primary tile''s camera at the current playhead; Bookmarks page lists/deletes them and links each one back into Playback via ?cameraId=&atUtc=, which resolves to the first visible View containing that camera and seeks there. New BookmarkRetentionService (6h sweep, same shape as AuditLogRetentionService) deletes a bookmark once its camera''s earliest remaining Segment starts after the bookmark''s own timestamp, or immediately if the camera has no segments left -- entries expire with the footage they point at, per the roadmap''s own requirement. Reuses Playback.View + CameraAccessActions.Playback, no new permission. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 116 AND Patch = 0");
        }
    }
}
