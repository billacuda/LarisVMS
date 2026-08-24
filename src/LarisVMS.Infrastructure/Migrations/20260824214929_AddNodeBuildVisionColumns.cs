using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeBuildVisionColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VisionFilePath",
                table: "NodeBuildVersions",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VisionSha256",
                table: "NodeBuildVersions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VisionSizeBytes",
                table: "NodeBuildVersions",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VisionFilePath",
                table: "NodeBuildVersions");

            migrationBuilder.DropColumn(
                name: "VisionSha256",
                table: "NodeBuildVersions");

            migrationBuilder.DropColumn(
                name: "VisionSizeBytes",
                table: "NodeBuildVersions");
        }
    }
}
