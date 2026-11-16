using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.MariaDb.Migrations
{
    /// <inheritdoc />
    public partial class AddViewTree : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsShared",
                table: "Views");

            migrationBuilder.AddColumn<Guid>(
                name: "FolderId",
                table: "Views",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "RoleId",
                table: "Views",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true,
                collation: "utf8mb4_uca1400_as_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "SkipViewMoveConfirm",
                table: "UserSessions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ViewFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ParentId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    RoleId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UserId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ViewFolders", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_uca1400_as_ci");

            migrationBuilder.CreateTable(
                name: "ViewTreeGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    OwnerRoleId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GrantedRoleId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true, collation: "utf8mb4_uca1400_as_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ViewTreeGrants", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_uca1400_as_ci");

            migrationBuilder.CreateIndex(
                name: "IX_Views_FolderId",
                table: "Views",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Views_RoleId",
                table: "Views",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_ViewFolders_ParentId",
                table: "ViewFolders",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_ViewFolders_RoleId",
                table: "ViewFolders",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_ViewFolders_UserId",
                table: "ViewFolders",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ViewTreeGrants_GrantedRoleId",
                table: "ViewTreeGrants",
                column: "GrantedRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_ViewTreeGrants_OwnerRoleId_GrantedRoleId",
                table: "ViewTreeGrants",
                columns: new[] { "OwnerRoleId", "GrantedRoleId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ViewFolders");

            migrationBuilder.DropTable(
                name: "ViewTreeGrants");

            migrationBuilder.DropIndex(
                name: "IX_Views_FolderId",
                table: "Views");

            migrationBuilder.DropIndex(
                name: "IX_Views_RoleId",
                table: "Views");

            migrationBuilder.DropColumn(
                name: "FolderId",
                table: "Views");

            migrationBuilder.DropColumn(
                name: "RoleId",
                table: "Views");

            migrationBuilder.DropColumn(
                name: "SkipViewMoveConfirm",
                table: "UserSessions");

            migrationBuilder.AddColumn<bool>(
                name: "IsShared",
                table: "Views",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }
    }
}
