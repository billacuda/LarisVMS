using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_124_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 124, 0, GETUTCDATE(), 'Fixed: a Bookmark/Snapshot Play deep link landed paused, needing an extra click -- resolveDeepLink now starts playback immediately. Changed: Snapshot card thumbnails are noticeably higher resolution -- /playback-thumbnail was capped at 150px (a hover-glance size) for every caller; exact=true requests (Snapshots'' own cards) now request 854px, ~480p on a 16:9 source, while hover-preview callers keep 150px. Resolution travels as a maxDim param and is folded into the on-disk cache filename so different sizes never collide; ThumbnailBackfillService updated to match. Install-node.ps1 re-run needed -- Node/NodeUpdater bumped to 0.124.0 in lockstep.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 124 AND Patch = 0");
        }
    }
}
