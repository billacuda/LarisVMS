using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.MariaDb.Migrations
{
    /// <inheritdoc />
    public partial class AddHomeAssistant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HomeAssistantSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    BrokerHost = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BrokerPort = table.Column<int>(type: "int", nullable: false),
                    UseTls = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    AllowUntrustedCertificate = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Username = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Password = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TopicPrefix = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DiscoveryPrefix = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RoleId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LastModifiedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastModifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HomeAssistantSettings", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_uca1400_as_ci");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HomeAssistantSettings");
        }
    }
}
