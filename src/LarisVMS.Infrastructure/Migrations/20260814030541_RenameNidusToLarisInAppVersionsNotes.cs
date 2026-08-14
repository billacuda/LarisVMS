using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameNidusToLarisInAppVersionsNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE AppVersions SET Notes = REPLACE(REPLACE(REPLACE(Notes, 'NidusVMS', 'LarisVMS'), 'NIDUSVMS', 'LARISVMS'), 'nidusvms', 'larisvms') " +
                "WHERE Notes LIKE '%idus%'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE AppVersions SET Notes = REPLACE(REPLACE(REPLACE(Notes, 'LarisVMS', 'NidusVMS'), 'LARISVMS', 'NIDUSVMS'), 'larisvms', 'nidusvms') " +
                "WHERE Notes LIKE '%aris%'");
        }
    }
}
