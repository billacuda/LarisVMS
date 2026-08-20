using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_103_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 103, 0, GETUTCDATE(), 'Security: self-registration is now disabled. The packaged Identity UI shipped a /Identity/Account/Register page that nothing in this app overrode or gated -- anyone who could reach the site could create an account, with no invite or approval step. A self-registered account got no role and so could not do anything (every permission check fails closed with zero roles), but that was an accident of there being nothing to grant a new account yet, not a deliberate control. The route now redirects to Login. Found while looking into the lack of an admin page for managing accounts, roles, and permissions -- that page is the real fix and is being built next. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 103 AND Patch = 0");
        }
    }
}
