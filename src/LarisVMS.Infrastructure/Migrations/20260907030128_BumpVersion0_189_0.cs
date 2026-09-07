using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_189_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 189, 0, GETUTCDATE(), 'Fixed: moving people and animals are reliably tagged as moving again - the 0.188.0 Snapshot motion accuracy check added a directedness test that a rigid vehicle passed but an unsteady person/animal detection box often failed, so real movers were logged as idle with no snapshot; the check now keys only on how far the object centre travels, a parked vehicle still held idle by the displacement floor. Fixed: the registration key on Admin - Settings - Nodes shows its value again (blank since 0.171.0). Changed: hover thumbnails and snapshot images are now WebP (smaller, faster); existing JPEGs keep being served and are replaced with WebP as they regenerate; a node whose ffmpeg has no WebP encoder keeps producing JPEG. Changed: the retention days-left estimate on Admin - Nodes now also shows for a node archive volume. Nodes auto-update.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 189 AND Patch = 0");
        }
    }
}
