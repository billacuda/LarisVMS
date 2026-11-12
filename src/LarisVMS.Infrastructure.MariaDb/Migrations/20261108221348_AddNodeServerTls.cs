using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.MariaDb.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeServerTls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientEndpointCertSha256",
                table: "Nodes",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "utf8mb4_uca1400_as_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "EncryptServerConnection",
                table: "Nodes",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientEndpointCertSha256",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "EncryptServerConnection",
                table: "Nodes");
        }
    }
}
