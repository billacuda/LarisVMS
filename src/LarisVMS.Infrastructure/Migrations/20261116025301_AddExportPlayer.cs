using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
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
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IncludePlayer",
                table: "ExportJobs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ConvertedFilePath",
                table: "ExportJobItems",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConvertedSha256",
                table: "ExportJobItems",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

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
