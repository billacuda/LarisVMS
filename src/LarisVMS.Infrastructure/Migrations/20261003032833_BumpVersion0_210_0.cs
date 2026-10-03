using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_210_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 210, 0, GETUTCDATE(), 'Added: MSI installers for the web server, recorder node and media proxy. Changed: the web server migrates its own database and is self-contained; setup explains .\SQLEXPRESS. Fixed: setup wizard on fresh installs (redirect loop, missing roles); live view for a node on the same machine; live AI boxes ~700 ms behind and moving in steps; DirectML on NVIDIA/Intel GPUs; in-progress Grid-mode motion.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 210 AND Patch = 0");
        }
    }
}
