using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_172_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 172, 0, GETUTCDATE(), 'Fixed: portrait and corridor-mounted cameras no longer produce severely horizontally-stretched AI-detection snapshot crops. The detection pipeline assumed a 1280x720 landscape shape for any camera whose watch stream had never been resolution-probed - every manually-added camera, and any whose ONVIF metadata disagreed with the delivered stream - squashing a portrait frame into a landscape buffer before the detector and every crop saw it. It now falls back to the real main-stream dimensions for the aspect ratio, and the recorder learns and stores the watch streams true resolution from ffmpeg on connect, so an affected camera self-corrects within a reconcile cycle and its resolution now shows on the dashboard.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 172 AND Patch = 0");
        }
    }
}
