using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_17_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 17, 0, GETUTCDATE(), 'Found and fixed the root cause of playback never loading video: ASP.NET binds a Z-suffixed query-string DateTime as Kind=Local (converted to the server''s zone), so every timeline/segment range query silently searched a window shifted by the server''s UTC offset - the timeline still looked plausible but no segment ever covered the requested instant. TimelineService now normalizes every range bound to true UTC first, covered by 4 new unit tests. Also fixed the AbortError console noise from 0.16.0''s fetch cancellation. No NidusVMS.Node changes - no recorder-node update needed. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 17 AND Patch = 0");
        }
    }
}
