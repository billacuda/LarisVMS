using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_107_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 107, 0, GETUTCDATE(), 'M15 pass 3: Gmail OAuth2 email provider, completing the provider abstraction (SMTP/Graph/Gmail all implemented). GmailEmailProvider (MailKit + SaslMechanismOAuth2, no Google SDK) exchanges the stored refresh token for a fresh access token on every send. New Pages/Admin/OAuthCallback handles the consent redirect: GoogleOAuthConnectProvider builds the authorization URL and exchanges the code for a refresh token, guarded by a DataProtector-protected, 10-minute-expiring state parameter. Admin -> Settings -> Email gained a Gmail card (client id/secret, Gmail address, Save and connect to Google). GmailRefreshToken is written only by the callback, never typed into the form. Ported from rsolva, adapted for EmailSettings being a singleton row. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 107 AND Patch = 0");
        }
    }
}
