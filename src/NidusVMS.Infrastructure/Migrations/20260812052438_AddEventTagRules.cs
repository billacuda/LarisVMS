using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEventTagRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EventTagRuleId",
                table: "MotionSpans",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EventTagRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StartTopic = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    StopTopic = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ColorHex = table.Column<string>(type: "nvarchar(9)", maxLength: 9, nullable: false),
                    DrivesRecording = table.Column<bool>(type: "bit", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventTagRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventTagRules_Cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "Cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MotionSpans_EventTagRuleId",
                table: "MotionSpans",
                column: "EventTagRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_EventTagRules_CameraId_Name",
                table: "EventTagRules",
                columns: new[] { "CameraId", "Name" });

            migrationBuilder.AddForeignKey(
                name: "FK_MotionSpans_EventTagRules_EventTagRuleId",
                table: "MotionSpans",
                column: "EventTagRuleId",
                principalTable: "EventTagRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MotionSpans_EventTagRules_EventTagRuleId",
                table: "MotionSpans");

            migrationBuilder.DropTable(
                name: "EventTagRules");

            migrationBuilder.DropIndex(
                name: "IX_MotionSpans_EventTagRuleId",
                table: "MotionSpans");

            migrationBuilder.DropColumn(
                name: "EventTagRuleId",
                table: "MotionSpans");
        }
    }
}
