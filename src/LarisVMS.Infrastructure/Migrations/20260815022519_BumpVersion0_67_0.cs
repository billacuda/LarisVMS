using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_67_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 67, 0, GETUTCDATE(), 'Added: Exports page auto-refreshes every 4s while a job is Queued/Running, a trash button deletes a finished export (best-effort removes each node-side output file first, falling back to the node''s own 7-day sweep), and a retry button re-queues a single failed item without resubmitting the whole export. Node change -- install-node.ps1 re-run needed on every recorder (new DELETE /export-file/{exportItemId} route).')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 67 AND Patch = 0");
        }
    }
}
