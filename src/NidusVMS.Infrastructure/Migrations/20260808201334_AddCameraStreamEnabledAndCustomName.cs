using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCameraStreamEnabledAndCustomName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomName",
                table: "CameraStreams",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            // defaultValue: true, not the generator's default false — every existing stream (all of
            // them actively recording or eligible to) must stay enabled when this column is added,
            // or NodeService.GetConfigAsync's new `Where(s => s.IsEnabled)` filter would silently
            // stop every camera's recording the moment this migration applied to a live database.
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                table: "CameraStreams",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomName",
                table: "CameraStreams");

            migrationBuilder.DropColumn(
                name: "IsEnabled",
                table: "CameraStreams");
        }
    }
}
