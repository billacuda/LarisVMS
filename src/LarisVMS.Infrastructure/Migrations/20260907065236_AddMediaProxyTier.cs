using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaProxyTier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BackupProxyId",
                table: "Nodes",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PrimaryProxyId",
                table: "Nodes",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MediaProxies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Host = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: false),
                    ApiKeyHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PreviousApiKeyHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CheckInNonce = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CertPfxPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CertPfxPassword = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AllowInsecure = table.Column<bool>(type: "bit", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastIpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Healthy = table.Column<bool>(type: "bit", nullable: false),
                    LastHealthyAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CertNotAfter = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CertIsSelfSigned = table.Column<bool>(type: "bit", nullable: true),
                    ReportedPort = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaProxies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_BackupProxyId",
                table: "Nodes",
                column: "BackupProxyId");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_PrimaryProxyId",
                table: "Nodes",
                column: "PrimaryProxyId");

            migrationBuilder.AddForeignKey(
                name: "FK_Nodes_MediaProxies_BackupProxyId",
                table: "Nodes",
                column: "BackupProxyId",
                principalTable: "MediaProxies",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Nodes_MediaProxies_PrimaryProxyId",
                table: "Nodes",
                column: "PrimaryProxyId",
                principalTable: "MediaProxies",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Nodes_MediaProxies_BackupProxyId",
                table: "Nodes");

            migrationBuilder.DropForeignKey(
                name: "FK_Nodes_MediaProxies_PrimaryProxyId",
                table: "Nodes");

            migrationBuilder.DropTable(
                name: "MediaProxies");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_BackupProxyId",
                table: "Nodes");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_PrimaryProxyId",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "BackupProxyId",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "PrimaryProxyId",
                table: "Nodes");
        }
    }
}
