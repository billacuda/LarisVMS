using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_10_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 10, 0, GETUTCDATE(), 'Recorder nodes now resume recording on their own after rebooting during a central-server outage, instead of sitting idle until the server answers again — node.config caches the last successful camera config (DPAPI-protected, same as the registration secret) and falls back to it on a cold start with nothing recording yet. An already-running node was never affected by this gap. See CHANGELOG for details and the StorageManager known limitation.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 10 AND Patch = 0");
        }
    }
}
