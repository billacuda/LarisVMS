using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMotionGridColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MotionGridMask",
                table: "Cameras",
                type: "nvarchar(max)",
                nullable: true);

            // 0.03/32, not the scaffolded 0.0/0 — matching Camera.MotionGridSensitivity/
            // MotionGridSize's own C# defaults for a brand-new camera, since EF's AddColumn scaffold
            // only fills in a *backfill* value for existing rows, never the entity's own initializer.
            // 0.0 would make Grid mode trigger on literally any change once switched to, and a 0-cell
            // grid is nonsensical (see MotionGrid's own doc comment) — neither is a real "off" state,
            // since MotionRegionMode already defaults to Polygon (0) below, so these two only ever
            // start mattering once an operator actually switches a camera to Grid mode.
            migrationBuilder.AddColumn<double>(
                name: "MotionGridSensitivity",
                table: "Cameras",
                type: "float",
                nullable: false,
                defaultValue: 0.03);

            migrationBuilder.AddColumn<int>(
                name: "MotionGridSize",
                table: "Cameras",
                type: "int",
                nullable: false,
                defaultValue: 32);

            migrationBuilder.AddColumn<int>(
                name: "MotionRegionMode",
                table: "Cameras",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MotionGridMask",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "MotionGridSensitivity",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "MotionGridSize",
                table: "Cameras");

            migrationBuilder.DropColumn(
                name: "MotionRegionMode",
                table: "Cameras");
        }
    }
}
