using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_137_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 137, 1, GETUTCDATE(), 'Fixed: adaptive streaming (0.137.0) left every switched tile stuck reconnecting. A tile moved to Sub still declared its SourceBuffer with the Main stream''s codec. Confirmed against this deployment''s own data: every camera records hevc on Main but H264 on Sub, so a switched tile opened an hvc1 SourceBuffer and was then fed H.264 bytes. MSE treats a declared-codec/track-list mismatch as a hard failure, so the first Sub fragment tripped a sourceBuffer error and NotSupportedError, and the retry wrapper relaunched into the same wall forever. Each camera''s Sub codec and audio flag are now sent alongside Main''s, and a tile picks whichever pair matches the role it is actually requesting, re-evaluated on every live restart so the quality override and the fullscreen force-to-Main switch both carry the right codec. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 137 AND Patch = 1");
        }
    }
}
