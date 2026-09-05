using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_184_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 184, 0, GETUTCDATE(), 'Changed: the AI-detection snapshot for a moving object now keeps upgrading toward the clearest view seen so far instead of freezing on its first sighting. A later frame only replaces the staged crop when its confidence-weighted box score clearly beats the one already kept, so a distant/blurry first sighting gets replaced once the object passes closer, right up until it stops moving or leaves the scene. No setting to configure; no visible change otherwise. Nodes update automatically; no install-node.ps1 re-run.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 184 AND Patch = 0");
        }
    }
}
