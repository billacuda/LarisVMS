using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_156_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 156, 0, GETUTCDATE(), 'Fixed: some snapshots stalled forever on Play, looping stall-recovery at the identical instant. Recovery now backs off after 3 same-target attempts, falling back to the segment''s own start then giving up with a permanent status message instead of looping forever; underlying stall cause not yet confirmed. Added: Snapshots search can now filter by single camera, all cameras, a camera group, or a saved view. Playback timeline is taller on mobile, overlays the video instead of pushing it down, and auto-hides after 5s idle (toggle, default on, remembered per user). Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 156 AND Patch = 0");
        }
    }
}
