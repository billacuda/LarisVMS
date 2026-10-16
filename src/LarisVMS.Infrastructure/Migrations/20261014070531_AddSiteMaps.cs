using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSiteMaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SiteMaps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    CenterLat = table.Column<double>(type: "float", nullable: true),
                    CenterLng = table.Column<double>(type: "float", nullable: true),
                    Zoom = table.Column<int>(type: "int", nullable: true),
                    Image = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ImageContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ImageWidth = table.Column<int>(type: "int", nullable: true),
                    ImageHeight = table.Column<int>(type: "int", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteMaps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CameraPlacements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MapId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Y = table.Column<double>(type: "float", nullable: false),
                    X = table.Column<double>(type: "float", nullable: false),
                    BearingDeg = table.Column<double>(type: "float", nullable: false),
                    FovDeg = table.Column<double>(type: "float", nullable: false),
                    Range = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CameraPlacements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CameraPlacements_Cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "Cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CameraPlacements_SiteMaps_MapId",
                        column: x => x.MapId,
                        principalTable: "SiteMaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CameraPlacements_CameraId",
                table: "CameraPlacements",
                column: "CameraId");

            migrationBuilder.CreateIndex(
                name: "IX_CameraPlacements_MapId_CameraId",
                table: "CameraPlacements",
                columns: new[] { "MapId", "CameraId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CameraPlacements");

            migrationBuilder.DropTable(
                name: "SiteMaps");
        }
    }
}
