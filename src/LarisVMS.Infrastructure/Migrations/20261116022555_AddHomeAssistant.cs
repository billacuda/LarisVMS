using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    BrokerHost = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    BrokerPort = table.Column<int>(type: "int", nullable: false),
                    UseTls = table.Column<bool>(type: "bit", nullable: false),
                    AllowUntrustedCertificate = table.Column<bool>(type: "bit", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Password = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TopicPrefix = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DiscoveryPrefix = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RoleId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    LastModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastModifiedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HomeAssistantSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HomeAssistantSettings");
        }
    }
}
