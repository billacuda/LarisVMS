using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_42_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 42, 0, GETUTCDATE(), 'Fixed: the v0.41.0 Event tags page had no direct way to reach it (reported: not findable anywhere on the Cameras page). Only entry point was a small button on Cameras/Edit''s header, alongside Zones (which had the same undiscoverable gap since M8 pass 1, just never reported). Cameras/Index''s per-row action column now links directly to both Zones and Event tags for every camera.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 42 AND Patch = 0");
        }
    }
}
