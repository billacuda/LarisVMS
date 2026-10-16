using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSpeedHourlyStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SpeedHourlyStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpeedTrapId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HourUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Count = table.Column<int>(type: "int", nullable: false),
                    SumKph = table.Column<double>(type: "float", nullable: false),
                    MaxKph = table.Column<double>(type: "float", nullable: false),
                    OverLimitCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeedHourlyStats", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpeedHourlyStats_CameraId_HourUtc",
                table: "SpeedHourlyStats",
                columns: new[] { "CameraId", "HourUtc" })
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_SpeedHourlyStats_HourUtc",
                table: "SpeedHourlyStats",
                column: "HourUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpeedHourlyStats");
        }
    }
}
