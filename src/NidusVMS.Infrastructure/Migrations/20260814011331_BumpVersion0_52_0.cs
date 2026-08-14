using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_52_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 52, 0, GETUTCDATE(), 'Fixed: Admin -> Nodes pointed at Setup -> Node for the recorder registration key, a leftover from before that control moved to Admin -> Settings -- two pages showing/rotating the same key, with the stale one being the one people got sent to. Now links straight to Admin -> Settings. Also refreshed Node Builds copy on both pages to describe the new deploy.ps1-registers/admin-approves flow instead of the old upload-a-build wording.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 52 AND Patch = 0");
        }
    }
}
