using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_55_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 55, 0, GETUTCDATE(), 'Added: two new Recording.Mode values completing M8''s original four-mode design. Schedule mode keeps a segment only if it starts inside a per-camera time window (new Cameras -> Schedule page), evaluated against the recorder node''s own local clock. Event mode keeps a segment only if a specific Drives-recording event tag rule fired nearby -- unlike Motion mode, it ignores Motion zones and the built-in ONVIF motion classifier entirely. Both fail open (keep everything, warn once) when nothing is configured, same as Motion mode does for a camera with no zone. LarisVMS.Node change -- install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 55 AND Patch = 0");
        }
    }
}
