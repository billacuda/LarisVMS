using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_167_3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 167, 3, GETUTCDATE(), 'Fixed Grid modes mask/size/sensitivity/active-mode never surviving a page refresh - reported as cells dont save and always reopens in Grid mode, one real cause: CameraService.GetAsync builds its Camera result through a hand-written field-by-field projection (kept there specifically to keep encrypted credential columns out of the query), never updated when the four Grid-mode columns were added. Skipping a field there does not fail loudly - the result silently gets that propertys plain C# default instead of its real stored value, for every caller of GetAsync including the Zones pages own motion-region endpoint. The Save button was working correctly the whole time; nothing ever read the saved values back. NodeService.GetConfigAsync loads the full entity via Include, not this projection, so Grid mode itself was already detecting motion correctly - only the editors own read-back was broken.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 167 AND Patch = 3");
        }
    }
}
