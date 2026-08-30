using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_161_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 161, 1, GETUTCDATE(), 'Fixed the Snapshots page throwing on every load right after 0.161.0 deployed: the pass 2c footage guard used LINQ shapes SQL Server''s EF Core provider could not translate (a correlated Any()+Min() subquery, then a Join+DateTime-minus-TimeSpan combination). Rewritten as a single correlated Any() overlap test, which also fixed a real gap in the first working version: many plain-motion/zone cards kept showing the No thumbnail available placeholder because that version only checked whether a span was newer than a camera''s overall earliest remaining segment, not whether footage actually covered that specific instant (a recording gap from a node restart or dropped connection could still slip through).')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 161 AND Patch = 1");
        }
    }
}
