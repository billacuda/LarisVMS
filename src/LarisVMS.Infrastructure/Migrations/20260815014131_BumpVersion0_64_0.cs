using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_64_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 64, 0, GETUTCDATE(), 'Fixed: 0.63.0''s broken-image backstop was itself a regression -- nearly every hover started showing No preview available regardless of actual fetch success. Interrupting an in-flight image load (by setting a newer src) can fire a stale error event asynchronously after a newer successful load already happened, and the 0.63.0 handler could not tell the two apart. Now gated on the same hoverToken pattern used elsewhere: showImage stamps the token valid when src is set, error handler ignores anything that no longer matches. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 64 AND Patch = 0");
        }
    }
}
