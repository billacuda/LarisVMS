using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityHourlyCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivityHourlyCounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HourUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Source = table.Column<byte>(type: "tinyint", nullable: false),
                    Group = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityHourlyCounts", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityHourlyCounts_CameraId_HourUtc",
                table: "ActivityHourlyCounts",
                columns: new[] { "CameraId", "HourUtc" })
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityHourlyCounts_HourUtc",
                table: "ActivityHourlyCounts",
                column: "HourUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityHourlyCounts");
        }
    }
}
