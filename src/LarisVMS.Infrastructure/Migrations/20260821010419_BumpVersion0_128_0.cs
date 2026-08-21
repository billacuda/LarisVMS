using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_128_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 128, 0, GETUTCDATE(), 'Changed: object-detection snapshots now sample 1s after the event starts instead of at the span midpoint. A detection span''s length is dominated by the camera''s ~10s event cooldown rather than how long the subject was in frame, so a vehicle is long gone by the midpoint. Plain motion and custom-tag spans still use the midpoint; a detection shorter than the offset clamps to its own end. Changed: list pages remember filter text and sort column alongside the page size and column choices they already remembered (Cameras, Dashboard, Bookmarks, Nodes, Views, Exports). Added: Snapshots and Audit Logs remember their filter selections across navigation and refresh, server-backed so they follow the user across devices; Clear wipes the saved value. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 128 AND Patch = 0");
        }
    }
}
