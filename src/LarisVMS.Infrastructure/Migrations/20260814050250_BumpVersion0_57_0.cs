using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_57_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 57, 0, GETUTCDATE(), 'Fixed: playback timeline/current-time readout never advanced during playback -- wireExportPanel referenced a bare o instead of the module-level opts, throwing an uncaught ReferenceError partway through init() before setInterval(updatePlayhead, 500) was ever reached. Pre-existing bug, not introduced by the LarisVMS rename. The Export panel button/panel wiring was also broken by the same typo and is fixed alongside it.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 57 AND Patch = 0");
        }
    }
}
