using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordingFailover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BackupNodeId",
                table: "Nodes",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DisableAiObjectDetection",
                table: "Nodes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "FailoverReason",
                table: "Nodes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FailoverSinceUtc",
                table: "Nodes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailoverState",
                table: "Nodes",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceBy",
                table: "Nodes",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MaintenanceMode",
                table: "Nodes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "MaintenanceSinceUtc",
                table: "Nodes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BackupNodeIdOverride",
                table: "Cameras",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_BackupNodeId",
                table: "Nodes",
                column: "BackupNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_Cameras_BackupNodeIdOverride",
                table: "Cameras",
                column: "BackupNodeIdOverride");

            migrationBuilder.AddForeignKey(
                name: "FK_Cameras_Nodes_BackupNodeIdOverride",
                table: "Cameras",
                column: "BackupNodeIdOverride",
                principalTable: "Nodes",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Nodes_Nodes_BackupNodeId",
                table: "Nodes",
                column: "BackupNodeId",
                principalTable: "Nodes",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Cameras_Nodes_BackupNodeIdOverride",
                table: "Cameras");

            migrationBuilder.DropForeignKey(
                name: "FK_Nodes_Nodes_BackupNodeId",
                table: "Nodes");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_BackupNodeId",
                table: "Nodes");

            migrationBuilder.DropIndex(
                name: "IX_Cameras_BackupNodeIdOverride",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "BackupNodeId",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "DisableAiObjectDetection",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "FailoverReason",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "FailoverSinceUtc",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "FailoverState",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "MaintenanceBy",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "MaintenanceMode",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "MaintenanceSinceUtc",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "BackupNodeIdOverride",
                table: "Cameras");
        }
    }
}
