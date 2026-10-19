using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_218_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 218, 0, GETUTCDATE(), 'Added: scene heatmaps, travel paths, moving-object choices, camera device settings (single and bulk), camera list filters, evidence locks, subscribable and browser notifications, motion alerts, and an AI backend warning. Changed: path-based movement detection with sensitivity, faster database with nightly upkeep, HTTP/2 and HTTP/3. Fixed: objects counted twice behind obstacles, duplicate segment errors, old scripts after updates, the node installer removing CUDA libraries, parked vehicles with speeds, and fast objects not tracked. Security: a broad hardening pass.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 218 AND Patch = 0");
        }
    }
}
