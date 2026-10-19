using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RemovalNotifiedAtUtc",
                table: "EvidenceLocks",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowSubscribe",
                table: "AlertRules",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "AlertRules",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "AlertRules",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AlertSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AlertRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Browser = table.Column<bool>(type: "bit", nullable: false),
                    Email = table.Column<bool>(type: "bit", nullable: false),
                    MutedUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertSubscriptions_AlertRules_AlertRuleId",
                        column: x => x.AlertRuleId,
                        principalTable: "AlertRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PushSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: false),
                    P256dh = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Auth = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DeviceLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSuccessAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertRules_OwnerUserId",
                table: "AlertRules",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertSubscriptions_AlertRuleId_UserId",
                table: "AlertSubscriptions",
                columns: new[] { "AlertRuleId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlertSubscriptions_UserId",
                table: "AlertSubscriptions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_Endpoint",
                table: "PushSubscriptions",
                column: "Endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushSubscriptions_UserId",
                table: "PushSubscriptions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertSubscriptions");

            migrationBuilder.DropTable(
                name: "PushSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_AlertRules_OwnerUserId",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "RemovalNotifiedAtUtc",
                table: "EvidenceLocks");

            migrationBuilder.DropColumn(
                name: "AllowSubscribe",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "AlertRules");
        }
    }
}
