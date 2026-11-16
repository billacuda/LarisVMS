using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClientSecret = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AllowedDomains = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LastModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastModifiedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoogleSsoSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GoogleSsoSettings");
        }
    }
}
