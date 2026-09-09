using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeClientEndpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowInsecureClientEndpoint",
                table: "Nodes",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientCertPfxPassword",
                table: "Nodes",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientCertPfxPath",
                table: "Nodes",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ClientEndpointCertIsSelfSigned",
                table: "Nodes",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClientEndpointCertNotAfter",
                table: "Nodes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientEndpointHost",
                table: "Nodes",
                type: "nvarchar(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientEndpointLastError",
                table: "Nodes",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClientEndpointReportedPort",
                table: "Nodes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectStreamingMode",
                table: "Nodes",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowInsecureClientEndpoint",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "ClientCertPfxPassword",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "ClientCertPfxPath",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "ClientEndpointCertIsSelfSigned",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "ClientEndpointCertNotAfter",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "ClientEndpointHost",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "ClientEndpointLastError",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "ClientEndpointReportedPort",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "DirectStreamingMode",
                table: "Nodes");
        }
    }
}
