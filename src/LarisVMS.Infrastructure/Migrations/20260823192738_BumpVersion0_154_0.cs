using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_154_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 154, 0, GETUTCDATE(), 'Added: M20 pass 3, Sign in with Microsoft (Entra ID). Sign-in only, never registration - a first Entra sign-in is matched by email against an existing admin-provisioned account and linked via Identity''s own AspNetUserLogins table; no match is rejected, not registered. New Admin > Settings > Security section (enabled, Tenant ID, Client ID, Client Secret encrypted). Takes effect immediately, no restart, via an options-cache eviction on save. New package reference: Microsoft.AspNetCore.Authentication.OpenIdConnect. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 154 AND Patch = 0");
        }
    }
}
