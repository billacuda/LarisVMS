using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTamperEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChainHash",
                table: "Segments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentSha256",
                table: "Segments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrevChainHash",
                table: "Segments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ManifestSignedUtc",
                table: "ExportJobItems",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutputSha256",
                table: "ExportJobItems",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChainCheckpoints",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SegmentId = table.Column<long>(type: "bigint", nullable: false),
                    ChainHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SegmentEndUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SignedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    KeyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Signature = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChainCheckpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExportManifests",
                columns: table => new
                {
                    ExportJobItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportManifests", x => x.ExportJobItemId);
                    table.ForeignKey(
                        name: "FK_ExportManifests_ExportJobItems_ExportJobItemId",
                        column: x => x.ExportJobItemId,
                        principalTable: "ExportJobItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SegmentChainHeads",
                columns: table => new
                {
                    CameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SegmentId = table.Column<long>(type: "bigint", nullable: false),
                    ChainHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SegmentEndUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SegmentChainHeads", x => x.CameraId);
                });

            migrationBuilder.CreateTable(
                name: "SigningKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Algorithm = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PublicKeyPem = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    PrivateKey = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RetiredAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigningKeys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChainCheckpoints_CameraId_SegmentEndUtc",
                table: "ChainCheckpoints",
                columns: new[] { "CameraId", "SegmentEndUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChainCheckpoints");

            migrationBuilder.DropTable(
                name: "ExportManifests");

            migrationBuilder.DropTable(
                name: "SegmentChainHeads");

            migrationBuilder.DropTable(
                name: "SigningKeys");

            migrationBuilder.DropColumn(
                name: "ChainHash",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "ContentSha256",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "PrevChainHash",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "ManifestSignedUtc",
                table: "ExportJobItems");

            migrationBuilder.DropColumn(
                name: "OutputSha256",
                table: "ExportJobItems");
        }
    }
}
