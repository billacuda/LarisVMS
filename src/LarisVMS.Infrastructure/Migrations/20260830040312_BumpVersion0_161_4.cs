using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_161_4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 161, 4, GETUTCDATE(), 'Diagnostic logging only, for a still-open issue: some Snapshots cards show No thumbnail available even though their footage plays fine via Playback, and reloading does not fix it. LarisVMS.Node''s /playback-thumbnail and /snapshot-image routes previously returned a bare 404/502 with no log line on every failure path. Both now log a warning identifying the cause: a segment path not matching the node''s current storage root, the segment file missing from disk despite its row, or ffmpeg producing no frame. No behavior change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 161 AND Patch = 4");
        }
    }
}
