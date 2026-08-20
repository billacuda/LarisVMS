using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_113_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 113, 0, GETUTCDATE(), 'Privacy-mask burn-in (M18) is switched off (NodeWorker.PrivacyMaskEnabled = false) and deferred -- 0.112.0''s fix addressed one real bug but did not resolve the reported symptom (masked camera stuck cycling Connecting/Backoff, confirmed live), and the actual root cause is still unknown. A Privacy zone is a safe no-op again, same as pre-M18 and same as CameraMotion today -- drawable and saveable, no effect on recording. All underlying work (PrivacyMaskFilterBuilder, EncoderSelection, RecordingSession''s transcode branch, their tests) is untouched, ready for whenever this is picked back up. Needs install-node.ps1 re-run: NO. Node/NodeUpdater bumped to 0.113.0.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 113 AND Patch = 0");
        }
    }
}
