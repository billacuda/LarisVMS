using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rcordr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeStorageStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "StorageFreeBytes",
                table: "Nodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StorageStatsUpdatedAt",
                table: "Nodes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StorageTotalBytes",
                table: "Nodes",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StorageFreeBytes",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "StorageStatsUpdatedAt",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "StorageTotalBytes",
                table: "Nodes");
        }
    }
}
