using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CameraMultiGroupMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create the new join table and copy every existing Cameras.GroupId value into it BEFORE
            // dropping that column — hand-reordered from what `dotnet ef migrations add` scaffolded
            // (which drops the column first, losing every existing camera's group assignment). A
            // camera with no group (GroupId IS NULL) contributes no row, matching this app's existing
            // "no membership row = no group" convention.
            migrationBuilder.CreateTable(
                name: "CameraGroupMemberships",
                columns: table => new
                {
                    CamerasId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupsId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CameraGroupMemberships", x => new { x.CamerasId, x.GroupsId });
                    table.ForeignKey(
                        name: "FK_CameraGroupMemberships_CameraGroups_GroupsId",
                        column: x => x.GroupsId,
                        principalTable: "CameraGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CameraGroupMemberships_Cameras_CamerasId",
                        column: x => x.CamerasId,
                        principalTable: "Cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CameraGroupMemberships_GroupsId",
                table: "CameraGroupMemberships",
                column: "GroupsId");

            migrationBuilder.Sql(@"
                INSERT INTO CameraGroupMemberships (CamerasId, GroupsId)
                SELECT Id, GroupId FROM Cameras WHERE GroupId IS NOT NULL");

            migrationBuilder.DropForeignKey(
                name: "FK_Cameras_CameraGroups_GroupId",
                table: "Cameras");

            migrationBuilder.DropIndex(
                name: "IX_Cameras_GroupId",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Cameras");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "Cameras",
                type: "uniqueidentifier",
                nullable: true);

            // Best-effort, not lossless: a camera in more than one group can only carry one GroupId
            // again, so this picks an arbitrary (lowest-Id) membership per camera. Rolling back this
            // migration was never going to fully restore pre-migration state once a real deployment
            // has used the multi-group feature this introduces.
            migrationBuilder.Sql(@"
                UPDATE c SET c.GroupId = m.GroupsId
                FROM Cameras c
                CROSS APPLY (
                    SELECT TOP 1 GroupsId FROM CameraGroupMemberships
                    WHERE CamerasId = c.Id ORDER BY GroupsId
                ) m");

            migrationBuilder.DropTable(
                name: "CameraGroupMemberships");

            migrationBuilder.CreateIndex(
                name: "IX_Cameras_GroupId",
                table: "Cameras",
                column: "GroupId");

            migrationBuilder.AddForeignKey(
                name: "FK_Cameras_CameraGroups_GroupId",
                table: "Cameras",
                column: "GroupId",
                principalTable: "CameraGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
