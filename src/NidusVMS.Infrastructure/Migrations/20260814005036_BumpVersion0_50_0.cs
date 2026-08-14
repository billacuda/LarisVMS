using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_50_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 50, 0, GETUTCDATE(), 'Fixed: dragging a zoomed Playback video tile could also kick off the browser''s own native video-drag gesture, leaving the no-drop cursor stuck and making the timelines below feel randomly undraggable -- video pan-drag now calls preventDefault to suppress it. Changed: node-build uploads on Admin -> Node Builds are gone (a several-hundred-MB self-contained exe hit IIS''s own requestFiltering size limit as a 413, before the request ever reached the app). deploy.ps1 now registers each build directly against the server''s own database/storage as part of every deploy; Admin -> Node Builds is now a Pending/Approved/Rejected approval queue instead of an upload form.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 50 AND Patch = 0");
        }
    }
}
