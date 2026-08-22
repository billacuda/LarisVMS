using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_140_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 140, 0, GETUTCDATE(), 'Added: a node now retries a transient storage I/O error a few times before giving up. Confirmed live: this fleet''s SMB path to its NAS dropped for a stretch (IOException: An unexpected network error occurred) -- recording recovered with a node restart, but ThumbnailBackfillService used to abort its entire pass over a brief blip, and the byte-offset partial-fetch path (0.138.0) does more, smaller reads against the same path, surfacing as a stuck-then-catches-up player with nothing in the console. New StorageRetry (three attempts, short backoff) wraps the fragment-index build, opening a segment for /playback-segment, and the backfill service''s per-camera directory scan (switched from a lazy enumerator to a materialized list so the whole walk can be retried as one unit). Scoped to IOException only. Node change; install-node.ps1 re-run not needed.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 140 AND Patch = 0");
        }
    }
}
