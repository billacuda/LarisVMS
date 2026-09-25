using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_208_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 208, 0, GETUTCDATE(), 'Fixed: AI detection going dead until a manual node restart after a GPU driver reset (TDR) — the vision process stayed up with every camera''s inference failing every frame. The vision service now exits after 60s with no successful inference on any camera while frames keep arriving, and the node restarts it. Fixed: an inference failure on every frame silenced the per-camera cadence line instead of reporting the failure count on it.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 208 AND Patch = 0");
        }
    }
}
