using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
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
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackendsPublisherSignature",
                table: "NodeBuildVersions",
                type: "varchar(200)",
                unicode: false,
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackendsSha256",
                table: "NodeBuildVersions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BackendsSizeBytes",
                table: "NodeBuildVersions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackendsStamp",
                table: "NodeBuildVersions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
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
