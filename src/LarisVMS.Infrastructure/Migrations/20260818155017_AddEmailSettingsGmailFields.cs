using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailSettingsGmailFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GmailClientId",
                table: "EmailSettings",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GmailClientSecret",
                table: "EmailSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GmailEmailAddress",
                table: "EmailSettings",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GmailRefreshToken",
                table: "EmailSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GmailClientId",
                table: "EmailSettings");

            migrationBuilder.DropColumn(
                name: "GmailClientSecret",
                table: "EmailSettings");

            migrationBuilder.DropColumn(
                name: "GmailEmailAddress",
                table: "EmailSettings");

            migrationBuilder.DropColumn(
                name: "GmailRefreshToken",
                table: "EmailSettings");
        }
    }
}
