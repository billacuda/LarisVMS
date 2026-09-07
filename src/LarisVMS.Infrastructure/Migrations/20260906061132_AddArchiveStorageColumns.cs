using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddArchiveStorageColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "Segments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "StorageTier",
                table: "Segments",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<long>(
                name: "ArchiveFreeBytes",
                table: "Nodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArchiveRootPath",
                table: "Nodes",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ArchiveStatsUpdatedAt",
                table: "Nodes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ArchiveTotalBytes",
                table: "Nodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StoragePressureActive",
                table: "Nodes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "StoragePressureSince",
                table: "Nodes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Segments_CameraId_StorageTier",
                table: "Segments",
                columns: new[] { "CameraId", "StorageTier" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Segments_CameraId_StorageTier",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "StorageTier",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "ArchiveFreeBytes",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "ArchiveRootPath",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "ArchiveStatsUpdatedAt",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "ArchiveTotalBytes",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "StoragePressureActive",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "StoragePressureSince",
                table: "Nodes");
        }
    }
}
