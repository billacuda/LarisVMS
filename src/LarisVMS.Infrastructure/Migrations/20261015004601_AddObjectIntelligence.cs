using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddObjectIntelligence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CategoryFilter",
                table: "AlertRules",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ColorFilter",
                table: "AlertRules",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelFilter",
                table: "AlertRules",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleDays",
                table: "AlertRules",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "ScheduleEnd",
                table: "AlertRules",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "ScheduleStart",
                table: "AlertRules",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SpeedThresholdKph",
                table: "AlertRules",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ZoneId",
                table: "AlertRules",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DetectedObjects",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MotionSpanId = table.Column<long>(type: "bigint", nullable: false),
                    TrackId = table.Column<int>(type: "int", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BestFrameAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BoxX = table.Column<double>(type: "float", nullable: true),
                    BoxY = table.Column<double>(type: "float", nullable: true),
                    BoxW = table.Column<double>(type: "float", nullable: true),
                    BoxH = table.Column<double>(type: "float", nullable: true),
                    Confidence = table.Column<double>(type: "float", nullable: true),
                    PrimaryColor = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    UpperColor = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LowerColor = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    SpeedKph = table.Column<double>(type: "float", nullable: true),
                    SpeedTrapId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DetectedObjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DetectedObjects_MotionSpans_MotionSpanId",
                        column: x => x.MotionSpanId,
                        principalTable: "MotionSpans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpeedTraps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    GatesJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    DistanceMeters = table.Column<double>(type: "float", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeedTraps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpeedTraps_Cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "Cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DetectedObjects_MotionSpanId_TrackId",
                table: "DetectedObjects",
                columns: new[] { "MotionSpanId", "TrackId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpeedTraps_CameraId",
                table: "SpeedTraps",
                column: "CameraId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DetectedObjects");

            migrationBuilder.DropTable(
                name: "SpeedTraps");

            migrationBuilder.DropColumn(
                name: "CategoryFilter",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "ColorFilter",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "LabelFilter",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "ScheduleDays",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "ScheduleEnd",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "ScheduleStart",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "SpeedThresholdKph",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "ZoneId",
                table: "AlertRules");
        }
    }
}
