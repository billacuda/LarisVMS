using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_156_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 156, 1, GETUTCDATE(), 'Fixed: every page load threw ArgumentNullException on ClientId since 0.154.0 shipped, not just an Entra sign-in attempt - AuthenticationMiddleware builds every registered auth scheme''s options on every request. EntraOidcOptionsConfigurator was registered under the wrong DI interface (IConfigureNamedOptions instead of IConfigureOptions), so it never actually ran; and even fixed, an unconfigured deployment still needed a non-empty ClientId placeholder to pass validation, same as Authority''s existing fallback. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 156 AND Patch = 1");
        }
    }
}
