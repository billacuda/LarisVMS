using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.MariaDb.Migrations
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
                nullable: true,
                collation: "utf8mb4_uca1400_as_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PreviousHash",
                table: "AuditLogs",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true,
                collation: "utf8mb4_uca1400_as_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AuditChainStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LastHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PrunedThroughHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Seal = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LockVersion = table.Column<long>(type: "bigint", nullable: false),
                    ForwardedThroughId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditChainStates", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_uca1400_as_ci");
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
