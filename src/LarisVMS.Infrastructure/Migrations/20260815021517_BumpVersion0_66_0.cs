using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_66_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 66, 0, GETUTCDATE(), 'Fixed: exporting a camera whose range crossed a node reassignment failed with an opaque Node rejected the export request (HTTP 400). ExportJobDispatcher sent every segment path to the camera''s current node, but a segment recorded before the move still lives on the old node''s disk. GetSegmentFilePathsAsync now also returns each segment''s owning NodeId, so a cross-node split is caught before dispatch and fails with a specific message naming which node still holds the older footage. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 66 AND Patch = 0");
        }
    }
}
