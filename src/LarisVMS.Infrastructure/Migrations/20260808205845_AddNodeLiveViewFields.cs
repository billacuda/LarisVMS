using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeLiveViewFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LivePort",
                table: "Nodes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MediaSigningKey",
                table: "Nodes",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LivePort",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "MediaSigningKey",
                table: "Nodes");
        }
    }
}
