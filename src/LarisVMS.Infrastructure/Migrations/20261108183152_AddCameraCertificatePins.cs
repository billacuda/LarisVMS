using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCameraCertificatePins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CameraCertificatePins",
                columns: table => new
                {
                    Host = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: false),
                    Thumbprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CameraCertificatePins", x => x.Host);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CameraCertificatePins");
        }
    }
}
