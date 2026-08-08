using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rcordr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeStorageRootOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StorageRootPath",
                table: "Nodes",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StorageRootPath",
                table: "Nodes");
        }
    }
}
