using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_105_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 105, 0, GETUTCDATE(), 'M15 pass 1: outbound email. New EmailSettings singleton row (Admin -> Settings -> Email) with SMTP host/port/ssl/username/password (encrypted at rest, same SecretProtection pattern as camera credentials) and a save-then-test-send flow. IEmailProvider/EmailProviderFactory ported from rsolva -- MailKit-based SmtpEmailProvider is the only provider implemented; Graph and Gmail OAuth2 are later passes. Nothing calls IEmailService yet outside the test-send button -- the alert-rule evaluator that will is a separate, not-yet-built pass. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 105 AND Patch = 0");
        }
    }
}
