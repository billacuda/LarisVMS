using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_161_5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 161, 5, GETUTCDATE(), 'Found via 0.161.4''s new logging: the No thumbnail available cards were concurrency, not data. The Snapshots page requests up to 24 AI-detection crops in one burst, but /snapshot-image capture was gated to 2 concurrent ffmpeg captures with a 3-second give-up, inherited from the hover-scrub-preview gate it was modeled on. A single capture measured live at 3.5-4.4 seconds, so only the first couple of a 24-card burst could ever get a slot before every other card 502''d, logged as an ffmpeg failure when ffmpeg was never even invoked. OnDemandSnapshotGate (used only by Snapshots-page AI crops) is now sized 6 concurrent with a 30s timeout. The sibling hover-scrub gate is unchanged. Both gates now log which one timed out and after how long.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 161 AND Patch = 5");
        }
    }
}
