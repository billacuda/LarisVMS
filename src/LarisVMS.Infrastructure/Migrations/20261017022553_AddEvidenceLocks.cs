using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEvidenceLocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "StorageBlockedByEvidenceLocks",
                table: "Nodes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "EvidenceLocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ToUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CreatedByUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RemovedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RemovedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RemovedByUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RemovalReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ExpiredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    GraceEndedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiryWarnedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceLocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvidenceLocks_Cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "Cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceLocks_CameraId_FromUtc_ToUtc",
                table: "EvidenceLocks",
                columns: new[] { "CameraId", "FromUtc", "ToUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvidenceLocks");

            migrationBuilder.DropColumn(
                name: "StorageBlockedByEvidenceLocks",
                table: "Nodes");
        }
    }
}
