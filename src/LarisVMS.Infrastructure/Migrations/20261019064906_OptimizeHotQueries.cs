using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeHotQueries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Segments_CameraId_StorageTier",
                table: "Segments");

            migrationBuilder.DropIndex(
                name: "IX_MotionSpans_CameraId_DetectedObjectLabel_StartUtc",
                table: "MotionSpans");

            migrationBuilder.CreateIndex(
                name: "IX_Segments_CameraId_StorageTier",
                table: "Segments",
                columns: new[] { "CameraId", "StorageTier" })
                .Annotation("SqlServer:Include", new[] { "SizeBytes" });

            migrationBuilder.CreateIndex(
                name: "IX_Segments_NodeId_CameraId",
                table: "Segments",
                columns: new[] { "NodeId", "CameraId" });

            migrationBuilder.CreateIndex(
                name: "IX_Segments_StorageTier_ArchivedAt",
                table: "Segments",
                columns: new[] { "StorageTier", "ArchivedAt" })
                .Annotation("SqlServer:Include", new[] { "NodeId", "SizeBytes" });

            migrationBuilder.CreateIndex(
                name: "IX_Segments_StorageTier_StartUtc",
                table: "Segments",
                columns: new[] { "StorageTier", "StartUtc" })
                .Annotation("SqlServer:Include", new[] { "NodeId", "SizeBytes" });

            migrationBuilder.CreateIndex(
                name: "IX_MotionSpans_CameraId_DetectedObjectLabel_StartUtc",
                table: "MotionSpans",
                columns: new[] { "CameraId", "DetectedObjectLabel", "StartUtc" },
                unique: true,
                filter: "[DetectedObjectLabel] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "EndUtc", "ZoneId", "EventTagRuleId", "DetectionKind" });

            migrationBuilder.CreateIndex(
                name: "IX_MotionSpans_EndUtc",
                table: "MotionSpans",
                column: "EndUtc")
                .Annotation("SqlServer:Include", new[] { "CameraId", "DetectionKind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Segments_CameraId_StorageTier",
                table: "Segments");

            migrationBuilder.DropIndex(
                name: "IX_Segments_NodeId_CameraId",
                table: "Segments");

            migrationBuilder.DropIndex(
                name: "IX_Segments_StorageTier_ArchivedAt",
                table: "Segments");

            migrationBuilder.DropIndex(
                name: "IX_Segments_StorageTier_StartUtc",
                table: "Segments");

            migrationBuilder.DropIndex(
                name: "IX_MotionSpans_CameraId_DetectedObjectLabel_StartUtc",
                table: "MotionSpans");

            migrationBuilder.DropIndex(
                name: "IX_MotionSpans_EndUtc",
                table: "MotionSpans");

            migrationBuilder.CreateIndex(
                name: "IX_Segments_CameraId_StorageTier",
                table: "Segments",
                columns: new[] { "CameraId", "StorageTier" });

            migrationBuilder.CreateIndex(
                name: "IX_MotionSpans_CameraId_DetectedObjectLabel_StartUtc",
                table: "MotionSpans",
                columns: new[] { "CameraId", "DetectedObjectLabel", "StartUtc" },
                unique: true,
                filter: "[DetectedObjectLabel] IS NOT NULL");
        }
    }
}
