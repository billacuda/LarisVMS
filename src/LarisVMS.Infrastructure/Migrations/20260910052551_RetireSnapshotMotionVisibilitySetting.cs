using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RetireSnapshotMotionVisibilitySetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Snapshots browser is now object-detection-only (AI-Vision detections, camera-native
            // ONVIF object classes, and custom event tags); plain motion is never listed there and is
            // no longer configurable. Drop the retired global toggle so it can't linger as a
            // confusing orphan row. Plain-motion MotionSpan rows are deliberately NOT touched — they
            // still drive the Playback timeline's motion bands and the live motion badge. The
            // per-DetectionKind 'Snapshots.Enabled.{kind}' rows are kept (still honoured).
            migrationBuilder.Sql("DELETE FROM Settings WHERE [Key] = 'Snapshots.Enabled.Motion';");
            migrationBuilder.Sql("DELETE FROM SettingOverrides WHERE [Key] = 'Snapshots.Enabled.Motion';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore: the key no longer exists in code, and the settings resolver treats
            // a missing 'Snapshots.Enabled.*' key as "enabled" — so a rollback behaves identically
            // whether or not the row is present.
        }
    }
}
