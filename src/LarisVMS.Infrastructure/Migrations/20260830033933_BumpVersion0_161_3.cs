using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_161_3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 161, 3, GETUTCDATE(), 'Fixed the Snapshots page still timing out after 0.161.2''s index. The index was not the cause: the pass 2c footage-coverage test is an interval-overlap test, and a B-tree can only seek one range boundary, so as a correlated subquery it runs per candidate row and CountAsync makes that every MotionSpan the filters allow. The coverage check now runs after pagination over just the rows actually rendered, where every predicate is a constant and each check is an index seek. Same visible behavior, and it now uses each camera''s own pre-roll. Trade-off: the page total is computed without the coverage filter, so a page can render fewer cards than its count suggests.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 161 AND Patch = 3");
        }
    }
}
