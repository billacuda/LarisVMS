using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.MariaDb.Migrations
{
    /// <inheritdoc />
    public partial class AddExportPlayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ConvertH265",
                table: "ExportJobs",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IncludePlayer",
                table: "ExportJobs",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ConvertedFilePath",
                table: "ExportJobItems",
                type: "varchar(450)",
                maxLength: 450,
                nullable: true,
                collation: "utf8mb4_uca1400_as_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ConvertedSha256",
                table: "ExportJobItems",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "utf8mb4_uca1400_as_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<long>(
                name: "ConvertedSizeBytes",
                table: "ExportJobItems",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConvertH265",
                table: "ExportJobs");

            migrationBuilder.DropColumn(
                name: "IncludePlayer",
                table: "ExportJobs");

            migrationBuilder.DropColumn(
                name: "ConvertedFilePath",
                table: "ExportJobItems");

            migrationBuilder.DropColumn(
                name: "ConvertedSha256",
                table: "ExportJobItems");

            migrationBuilder.DropColumn(
                name: "ConvertedSizeBytes",
                table: "ExportJobItems");
        }
    }
}
