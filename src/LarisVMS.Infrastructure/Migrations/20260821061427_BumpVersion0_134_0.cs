using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_134_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 134, 0, GETUTCDATE(), 'Fixed: timeline drag showed a no-drop cursor and stopped working (missing preventDefault on pointerdown). Fixed: playback could die with a SourceBuffer appendBuffer error and Play would silently do nothing after -- added auto-recovery via a native error listener. Fixed: the main Playback timeline loaded showing only blue coverage until a zoom triggered a redraw -- a reload() race where the correct reload arrived while the wrong initial one was in flight and got silently dropped; now queued instead. Changed: a Bookmark/Snapshot Play link now opens just that one camera, not a View containing it. Changed: Live page gained a Camera dropdown beside the View picker with a filter box. Changed: Snapshots/Audit Logs pagination is now windowed instead of listing every page. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 134 AND Patch = 0");
        }
    }
}
