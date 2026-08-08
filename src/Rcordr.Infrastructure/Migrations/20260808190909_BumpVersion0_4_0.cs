using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rcordr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_4_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 4, 0, GETUTCDATE(), 'M4 storage manager: per-camera retention (Camera -> Node -> Global via ISettingsResolver''s new Node scope), per-camera quota, and a global watermark backstop, all enforced by a new StorageManager background service on every node; nodes self-report disk usage on heartbeat; Admin -> Retention, Admin -> Nodes, and Cameras/Edit gained retention/quota controls and usage displays.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 4 AND Patch = 0");
        }
    }
}
