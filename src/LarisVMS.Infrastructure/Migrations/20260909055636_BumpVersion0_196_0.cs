using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_196_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 196, 0, GETUTCDATE(), 'Fixed: the storage watermark backstop deleted archive-enabled footage even when the archive volume was healthy and had room. 0.195.0 made the watermark pass always delete rather than archive, so a primary volume that crossed the watermark before its footage aged out permanently lost still-in-policy footage from archive-enabled cameras while the archive volume sat free. A single transient SMB/USB blip also gave up immediately at the archive reachability probe, the free-space check, and each per-file copy. Now the probe is retried over ~6.5s, the free-space check re-measures before concluding full, and each copy is retried on a short backoff; the watermark pass again moves an archive-enabled camera''s oldest footage to the archive volume while the primary has headroom, falling back to deletion only when the primary is within 7% of full or the archive volume is unreachable or full - preserving 0.195.0''s guarantee that the node keeps recording.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 196 AND Patch = 0");
        }
    }
}
