using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSpeedReporting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "SpeedLimitKph",
                table: "SpeedTraps",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CameraId",
                table: "DetectedObjects",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTime>(
                name: "SpeedMeasuredAtUtc",
                table: "DetectedObjects",
                type: "datetime2",
                nullable: true);

            // Existing rows: camera from their span; measurement time approximated by the best frame
            // (the crossing moment wasn't recorded before this column).
            migrationBuilder.Sql(
                "UPDATE o SET o.CameraId = m.CameraId FROM DetectedObjects o JOIN MotionSpans m ON m.Id = o.MotionSpanId;");
            migrationBuilder.Sql(
                "UPDATE DetectedObjects SET SpeedMeasuredAtUtc = COALESCE(BestFrameAtUtc, FirstSeenUtc) " +
                "WHERE SpeedKph IS NOT NULL AND SpeedMeasuredAtUtc IS NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_DetectedObjects_CameraId_SpeedMeasuredAtUtc",
                table: "DetectedObjects",
                columns: new[] { "CameraId", "SpeedMeasuredAtUtc" },
                filter: "[SpeedKph] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DetectedObjects_SpeedMeasuredAtUtc",
                table: "DetectedObjects",
                column: "SpeedMeasuredAtUtc",
                filter: "[SpeedKph] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DetectedObjects_CameraId_SpeedMeasuredAtUtc",
                table: "DetectedObjects");

            migrationBuilder.DropIndex(
                name: "IX_DetectedObjects_SpeedMeasuredAtUtc",
                table: "DetectedObjects");

            migrationBuilder.DropColumn(
                name: "SpeedLimitKph",
                table: "SpeedTraps");

            migrationBuilder.DropColumn(
                name: "CameraId",
                table: "DetectedObjects");

            migrationBuilder.DropColumn(
                name: "SpeedMeasuredAtUtc",
                table: "DetectedObjects");
        }
    }
}
