using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_87_5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 87, 5, GETUTCDATE(), 'Fixed: live tiles failed to decode (CHUNK_DEMUXER_ERROR_APPEND_FAILED), most often right after a reconnect. The node fanned raw 64KB pipe reads out to live viewers rather than complete fMP4 fragments, so a viewer joining mid-stream got the init segment followed by bytes starting partway through a moof or mdat, which the decoder rejects outright. The same flaw made back-pressure destructive, since dropping a raw chunk holes a box. Viewers are now fed whole moof+mdat fragments. Also fixed: live-view sessions leaked their video element event listeners, one full set per reconnect, so a single decode error printed once per leaked listener with stale counts and buried the real fault. Changed: vendor-integration cameras are marked with a puzzle piece rather than a plug. Node change -- install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 87 AND Patch = 5");
        }
    }
}
