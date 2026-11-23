using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.MariaDb.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeBuildBackends : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackendsFilePath",
                table: "NodeBuildVersions",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true,
                collation: "utf8mb4_uca1400_as_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "BackendsPublisherSignature",
                table: "NodeBuildVersions",
                type: "varchar(200)",
                unicode: false,
                maxLength: 200,
                nullable: true,
                collation: "utf8mb4_uca1400_as_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "BackendsSha256",
                table: "NodeBuildVersions",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "utf8mb4_uca1400_as_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<long>(
                name: "BackendsSizeBytes",
                table: "NodeBuildVersions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackendsStamp",
                table: "NodeBuildVersions",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "utf8mb4_uca1400_as_ci")
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackendsFilePath",
                table: "NodeBuildVersions");

            migrationBuilder.DropColumn(
                name: "BackendsPublisherSignature",
                table: "NodeBuildVersions");

            migrationBuilder.DropColumn(
                name: "BackendsSha256",
                table: "NodeBuildVersions");

            migrationBuilder.DropColumn(
                name: "BackendsSizeBytes",
                table: "NodeBuildVersions");

            migrationBuilder.DropColumn(
                name: "BackendsStamp",
                table: "NodeBuildVersions");
        }
    }
}
