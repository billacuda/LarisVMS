using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotAlignmentAndDedup_0_168_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The unique index below would fail to build if the "duplicate Snapshots" bug it exists
            // to prevent has already produced exact-duplicate rows. Collapse those first: for any
            // (CameraId, DetectedObjectLabel, StartUtc) group with more than one row, keep the
            // lowest Id (widest EndUtc/best box are already merged onto whichever row extended) and
            // delete the rest. Only exact-key duplicates are touched — fragmented spans with
            // different StartUtc values are left alone (server-side coalescing handles those going
            // forward).
            migrationBuilder.Sql(@"
                DELETE m
                FROM MotionSpans m
                WHERE m.DetectedObjectLabel IS NOT NULL
                  AND EXISTS (
                      SELECT 1 FROM MotionSpans k
                      WHERE k.CameraId = m.CameraId
                        AND k.DetectedObjectLabel = m.DetectedObjectLabel
                        AND k.StartUtc = m.StartUtc
                        AND k.Id < m.Id
                  );");

            migrationBuilder.CreateIndex(
                name: "IX_MotionSpans_CameraId_DetectedObjectLabel_StartUtc",
                table: "MotionSpans",
                columns: new[] { "CameraId", "DetectedObjectLabel", "StartUtc" },
                unique: true,
                filter: "[DetectedObjectLabel] IS NOT NULL");

            // Keep vision-debug images on for this existing install (the DTO/resolver default is
            // false, so new installs start with it off). Detection.EnableVisionDebugImages is a
            // plain global Setting row, same shape as every other Detection.* default.
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM Settings WHERE [Key] = 'Detection.EnableVisionDebugImages')
                    INSERT INTO Settings (Id, [Key], [Value], IsSystemSetting, CreatedAt)
                    VALUES (NEWID(), 'Detection.EnableVisionDebugImages', 'True', 0, GETUTCDATE());");

            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 168, 0, GETUTCDATE(), 'Snapshots now line up with the detected object: with high-res re-detection on, the snapshot is cropped from the exact Main-stream frame it decoded (the frame the vision-debug images use), shipped to the node, and promoted into the retention-governed cache on first view; the segment-seek fallback seeks with millisecond precision now, not whole seconds. Duplicate Snapshots cards fixed: the server coalesces AI detections of one object/camera starting within an idle-timeout gap into a single span, a filtered unique index makes the insert race-safe (exact-duplicate rows removed here first), and a camera-native Human/Vehicle/Animal span is hidden from Snapshots when an overlapping AI detection of the same category already carries the card. New Detection-settings toggle for the vision-debug images (off by default for new installs) plus a purge button.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 168 AND Patch = 0");
            migrationBuilder.Sql("DELETE FROM Settings WHERE [Key] = 'Detection.EnableVisionDebugImages'");

            migrationBuilder.DropIndex(
                name: "IX_MotionSpans_CameraId_DetectedObjectLabel_StartUtc",
                table: "MotionSpans");
        }
    }
}
