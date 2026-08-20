using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_106_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 106, 0, GETUTCDATE(), 'M15 pass 2: Microsoft Graph email provider. GraphEmailProvider (Microsoft.Graph + Azure.Identity ClientSecretCredential, app-only client-credentials auth -- no refresh token, the client secret is the durable credential) sends via Users[mailbox].SendMail, ported send-only from rsolva (no inbound fetch, LarisVMS only sends alerts). Admin -> Settings -> Email gained a Provider selector and a Graph fields card (tenant/client id, client secret encrypted at rest, optional shared mailbox). Gmail OAuth2 -- the one that needs an actual per-user consent flow and refresh-token storage -- is still the next pass. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 106 AND Patch = 0");
        }
    }
}
