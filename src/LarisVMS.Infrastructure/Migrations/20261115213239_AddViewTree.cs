using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
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
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoleId",
                table: "Views",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SkipViewMoveConfirm",
                table: "UserSessions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ViewFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RoleId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ViewFolders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ViewTreeGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerRoleId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    GrantedRoleId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ViewTreeGrants", x => x.Id);
                });

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
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
