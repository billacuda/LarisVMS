using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_142_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 142, 2, GETUTCDATE(), 'Fixed: clicking the Playback timeline to an earlier point within an already-loaded segment could freeze the video on its last frame, with no error and no recovery. A same-segment fast path moved currentTime without checking whether that instant was actually buffered -- a partial-fetch load only appends bytes from the fragment the node skipped ahead to, but still calls endOfStream(), so the browser treats the whole segment as seekable. Now falls back to a real reload when the target is not buffered, plus a stall watchdog for anything else that goes silently stuck. Also fixed: the node''s fragment selection compared an absolute media timestamp against a segment-relative seek offset, which could land seeks early or disable partial fetch entirely for some files. Node change; install-node.ps1 re-run not needed, ordinary auto-update covers it.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 142 AND Patch = 2");
        }
    }
}
