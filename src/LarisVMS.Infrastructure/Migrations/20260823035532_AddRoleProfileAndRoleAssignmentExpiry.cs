using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleProfileAndRoleAssignmentExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoleAssignmentExpiries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RoleId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AssignedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AssignedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAssignmentExpiries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoleProfiles",
                columns: table => new
                {
                    RoleId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Tag = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MaxScopeTier = table.Column<int>(type: "int", nullable: false),
                    AutoExpires = table.Column<bool>(type: "bit", nullable: false),
                    DefaultExpiryMinutes = table.Column<int>(type: "int", nullable: true),
                    PtzPriorityLevel = table.Column<int>(type: "int", nullable: true),
                    PtzLockoutSeconds = table.Column<int>(type: "int", nullable: true),
                    IsSystemRole = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleProfiles", x => x.RoleId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignmentExpiries_ExpiresAtUtc",
                table: "RoleAssignmentExpiries",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignmentExpiries_UserId_RoleId",
                table: "RoleAssignmentExpiries",
                columns: new[] { "UserId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleProfiles_Tag",
                table: "RoleProfiles",
                column: "Tag",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoleAssignmentExpiries");

            migrationBuilder.DropTable(
                name: "RoleProfiles");
        }
    }
}
