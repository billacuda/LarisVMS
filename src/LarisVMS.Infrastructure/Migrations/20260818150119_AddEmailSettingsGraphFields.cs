using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailSettingsGraphFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GraphClientId",
                table: "EmailSettings",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GraphClientSecret",
                table: "EmailSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GraphSharedMailbox",
                table: "EmailSettings",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GraphTenantId",
                table: "EmailSettings",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GraphClientId",
                table: "EmailSettings");

            migrationBuilder.DropColumn(
                name: "GraphClientSecret",
                table: "EmailSettings");

            migrationBuilder.DropColumn(
                name: "GraphSharedMailbox",
                table: "EmailSettings");

            migrationBuilder.DropColumn(
                name: "GraphTenantId",
                table: "EmailSettings");
        }
    }
}
