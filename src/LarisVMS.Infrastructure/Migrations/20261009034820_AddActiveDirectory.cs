using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DisabledByDirectorySync",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ActiveDirectorySettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Domain = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    UseLdaps = table.Column<bool>(type: "bit", nullable: false),
                    UseServiceAccount = table.Column<bool>(type: "bit", nullable: false),
                    ServiceAccountUsername = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ServiceAccountPassword = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SyncIntervalMinutes = table.Column<int>(type: "int", nullable: false),
                    LocalLoginsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastSyncStartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSyncCompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSyncError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LastSyncSummary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LastModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastModifiedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActiveDirectorySettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdGroupRoleLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupSid = table.Column<string>(type: "nvarchar(184)", maxLength: 184, nullable: false),
                    GroupName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RoleId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    IsMissing = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdGroupRoleLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdGroupRoleLinks_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdGroupRoleLinks_GroupSid_RoleId",
                table: "AdGroupRoleLinks",
                columns: new[] { "GroupSid", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdGroupRoleLinks_RoleId",
                table: "AdGroupRoleLinks",
                column: "RoleId");

            // Seeded so the documented recovery command
            // (UPDATE ActiveDirectorySettings SET LocalLoginsEnabled = 1) always has a row to update.
            migrationBuilder.Sql(@"
INSERT INTO ActiveDirectorySettings (Id, IsEnabled, UseLdaps, UseServiceAccount, SyncIntervalMinutes, LocalLoginsEnabled)
VALUES (NEWID(), 0, 1, 0, 30, 1);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActiveDirectorySettings");

            migrationBuilder.DropTable(
                name: "AdGroupRoleLinks");

            migrationBuilder.DropColumn(
                name: "DisabledByDirectorySync",
                table: "AspNetUsers");
        }
    }
}
