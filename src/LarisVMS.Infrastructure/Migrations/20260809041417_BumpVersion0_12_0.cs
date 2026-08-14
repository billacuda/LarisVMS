using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_12_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 12, 0, GETUTCDATE(), 'M7 pass 1: Playback & timeline. Pages/Playback scrubs a camera''s recorded history on a canvas timeline (wheel zoom, drag pan) and plays it back, synchronized across multiple cameras. Built but NOT yet verified in a real browser (no browser available in this environment) - only build and 12 new unit tests so far. See CHANGELOG for the architecture choice (per-segment MSE fetch instead of server-side stream synthesis) and known limitations before relying on this.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 12 AND Patch = 0");
        }
    }
}
