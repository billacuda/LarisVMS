using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_61_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 61, 0, GETUTCDATE(), 'Added: concurrency limits + low-priority background backfill for hover thumbnails. On-demand extraction capped at 2 concurrent, gives up after 3s rather than queueing unbounded. New ThumbnailBackfillService fills in missing thumbnails for 5-minute-aligned segments in the background, low OS priority, one at a time with pauses. Browser-side hover fetch now uses AbortController so a superseded request cancels the node ffmpeg process too. LarisVMS.Node change -- install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 61 AND Patch = 0");
        }
    }
}
