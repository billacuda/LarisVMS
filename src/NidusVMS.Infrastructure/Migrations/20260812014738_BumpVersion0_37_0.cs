using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_37_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 37, 0, GETUTCDATE(), 'CRITICAL FIX: every node process restart could silently delete already-recorded footage that already had valid database rows. RecordingSession rescans its entire on-disk history on every fresh start with no memory of what the server already knows about, and for a Motion-mode camera a freshly-restarted motion session has seen no activity yet at that instant, so nearly all re-fired history looked like no motion and was wrongly discarded. NodeWorker now fetches its already-known segment paths once before any recording starts and pre-seeds each session with them, so a file already known to the server is never re-evaluated again, regardless of restarts or Motion/Continuous mode changes. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 37 AND Patch = 0");
        }
    }
}
