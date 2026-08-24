using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddObjectDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AiAccelerator",
                table: "Nodes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetectedAcceleratorsJson",
                table: "Nodes",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "BestBoxConfidence",
                table: "MotionSpans",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "BestBoxH",
                table: "MotionSpans",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "BestBoxW",
                table: "MotionSpans",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "BestBoxX",
                table: "MotionSpans",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "BestBoxY",
                table: "MotionSpans",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BestFrameAtUtc",
                table: "MotionSpans",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DetectedObjectCategoryId",
                table: "MotionSpans",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetectedObjectLabel",
                table: "MotionSpans",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AiDetectionEnabled",
                table: "Cameras",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MotionDetectionSource",
                table: "Cameras",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DetectedObjectCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ColorHex = table.Column<string>(type: "nvarchar(9)", maxLength: 9, nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DetectedObjectCategories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MotionSpans_DetectedObjectCategoryId",
                table: "MotionSpans",
                column: "DetectedObjectCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_DetectedObjectCategories_Name",
                table: "DetectedObjectCategories",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MotionSpans_DetectedObjectCategories_DetectedObjectCategoryId",
                table: "MotionSpans",
                column: "DetectedObjectCategoryId",
                principalTable: "DetectedObjectCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MotionSpans_DetectedObjectCategories_DetectedObjectCategoryId",
                table: "MotionSpans");

            migrationBuilder.DropTable(
                name: "DetectedObjectCategories");

            migrationBuilder.DropIndex(
                name: "IX_MotionSpans_DetectedObjectCategoryId",
                table: "MotionSpans");

            migrationBuilder.DropColumn(
                name: "AiAccelerator",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "DetectedAcceleratorsJson",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "BestBoxConfidence",
                table: "MotionSpans");

            migrationBuilder.DropColumn(
                name: "BestBoxH",
                table: "MotionSpans");

            migrationBuilder.DropColumn(
                name: "BestBoxW",
                table: "MotionSpans");

            migrationBuilder.DropColumn(
                name: "BestBoxX",
                table: "MotionSpans");

            migrationBuilder.DropColumn(
                name: "BestBoxY",
                table: "MotionSpans");

            migrationBuilder.DropColumn(
                name: "BestFrameAtUtc",
                table: "MotionSpans");

            migrationBuilder.DropColumn(
                name: "DetectedObjectCategoryId",
                table: "MotionSpans");

            migrationBuilder.DropColumn(
                name: "DetectedObjectLabel",
                table: "MotionSpans");

            migrationBuilder.DropColumn(
                name: "AiDetectionEnabled",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "MotionDetectionSource",
                table: "Cameras");
        }
    }
}
