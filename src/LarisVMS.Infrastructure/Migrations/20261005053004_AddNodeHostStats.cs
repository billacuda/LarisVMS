using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeHostStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CpuPercent",
                table: "Nodes",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HostStatsUpdatedAt",
                table: "Nodes",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MemoryTotalBytes",
                table: "Nodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MemoryUsedBytes",
                table: "Nodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NetReceiveBytesPerSec",
                table: "Nodes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NetSendBytesPerSec",
                table: "Nodes",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CpuPercent",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "HostStatsUpdatedAt",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "MemoryTotalBytes",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "MemoryUsedBytes",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "NetReceiveBytesPerSec",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "NetSendBytesPerSec",
                table: "Nodes");
        }
    }
}
