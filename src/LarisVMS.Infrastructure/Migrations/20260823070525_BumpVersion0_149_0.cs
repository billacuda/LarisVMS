using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_149_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 149, 0, GETUTCDATE(), 'Added: M20 pass 1, REST API foundation + API keys. Admin > API Keys issues a machine credential bound to a Role, shown once at creation and SHA-256 hashed thereafter. New GET /api/v1/status, authenticated via an X-Api-Key header instead of the login cookie, returns per-camera connected/recording status (CameraAccess-scoped by the key''s role) and every node''s online/storage status, gated on the existing Dashboard.View permission. An API key carries only a Role, no user identity - the permission and camera-access resolution paths were extended to support that. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 149 AND Patch = 0");
        }
    }
}
