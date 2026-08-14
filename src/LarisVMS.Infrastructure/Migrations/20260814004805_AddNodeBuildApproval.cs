using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeBuildApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                table: "NodeBuildVersions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "NodeBuildVersions",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "NodeBuildVersions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Every row that already exists here was registered under the old model, where an
            // upload was immediately live with no approval step at all — the new default (0 =
            // Pending) would otherwise silently pull an already-in-use build out of rotation for
            // any node that hasn't updated to it yet.
            migrationBuilder.Sql("UPDATE NodeBuildVersions SET Status = 1 WHERE Status = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "NodeBuildVersions");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "NodeBuildVersions");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "NodeBuildVersions");
        }
    }
}
