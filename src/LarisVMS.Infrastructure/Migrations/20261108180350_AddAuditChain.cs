using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Hash",
                table: "AuditLogs",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousHash",
                table: "AuditLogs",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuditChainStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    LastHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    PrunedThroughHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    Seal = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    LockVersion = table.Column<long>(type: "bigint", nullable: false),
                    ForwardedThroughId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditChainStates", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditChainStates");

            migrationBuilder.DropColumn(
                name: "Hash",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "PreviousHash",
                table: "AuditLogs");
        }
    }
}
