using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_187_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 187, 1, GETUTCDATE(), 'Fixed: an AI-detection snapshot with several tagged objects now crops around all of them. The eager crop always covers the union of every moving object in the frame - the old fallback that framed only the single highest-confidence object when they were spread across the scene is gone - and the frame kept as the snapshot is the one that captures the most moving objects at once, ties broken by their combined confidence and size, so a later frame showing one object up close no longer replaces an earlier one that framed everything. Per-object peak confidence is still reported. Changed: AI detections of people are labelled just Human instead of Human - person on timeline cards, snapshot cards and the live-view box overlay; a category whose specific class adds detail (Vehicle - truck) is unchanged. Nodes auto-update; no install-node.ps1 re-run.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 187 AND Patch = 1");
        }
    }
}
