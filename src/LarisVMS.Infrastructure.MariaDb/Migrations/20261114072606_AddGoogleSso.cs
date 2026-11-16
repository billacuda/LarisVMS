using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.MariaDb.Migrations
{
    /// <inheritdoc />
    public partial class AddGoogleSso : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GoogleSsoSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ClientId = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ClientSecret = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AllowedDomains = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LastModifiedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastModifiedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoogleSsoSettings", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_uca1400_as_ci");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GoogleSsoSettings");
        }
    }
}
