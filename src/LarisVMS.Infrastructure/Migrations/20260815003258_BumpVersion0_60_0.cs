using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_60_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 60, 0, GETUTCDATE(), 'Fixed: hover thumbnails sometimes rendered as a broken image after Loading... -- the node wrote a newly-extracted thumbnail straight to its final cache path, so a second concurrent request for the same bucket could read a still-being-written, truncated file. Now writes to a temp file and atomically renames it into place. LarisVMS.Node change -- install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 60 AND Patch = 0");
        }
    }
}
