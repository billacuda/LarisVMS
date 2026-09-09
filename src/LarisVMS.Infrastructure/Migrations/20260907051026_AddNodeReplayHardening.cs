using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeReplayHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowReregistration",
                table: "Nodes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApiKeyRotatedAt",
                table: "Nodes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckInNonce",
                table: "Nodes",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PendingSecretRotation",
                table: "Nodes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SecretRotationDays",
                table: "Nodes",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowReregistration",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "ApiKeyRotatedAt",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "CheckInNonce",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "PendingSecretRotation",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SecretRotationDays",
                table: "Nodes");
        }
    }
}
