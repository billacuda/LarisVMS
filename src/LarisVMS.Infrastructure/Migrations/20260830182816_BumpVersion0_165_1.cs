using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_165_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 165, 1, GETUTCDATE(), 'Fixed two zone-editor bugs found live while testing 3c-1. Zone Kind silently serialized as a bare integer instead of its name over the zones API (no JsonConverter on ZoneKind, no global string-enum converter), so KIND_COLORS/KIND_LABELS lookups and the edit forms Kind dropdown were all keyed by a string that never matched - every zone rendered with ServerMotions own amber color regardless of its real kind, and editing a non-ServerMotion zone never selected the right dropdown option. New ZoneDto fixes the response side the same way SaveZoneRequest already handles requests. Also switched the live wash from a continuous ratio to the plans actual spec - no fill below a zones Sensitivity, fixed yellow at or above it - since the ratio version was never fully transparent at rest, masking whether live scoring was working at all.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 165 AND Patch = 1");
        }
    }
}
