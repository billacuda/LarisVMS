using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_110_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 110, 0, GETUTCDATE(), 'M17 foundation: hardware-transcode capability probing, no consumer feature wired up yet (that is M18+). FfmpegCapabilityProber (LarisVMS.Media) runs ffmpeg -encoders once at node startup and reports which of libx264/libx265/h264+hevc_qsv/nvenc/amf this node actually has; Node.DetectedEncodersJson stores the result, refreshed every heartbeat like Version. Admin -> Nodes shows each node''s detected encoders as badges. New EncodePipeline (LarisVMS.Media): a decode-hwaccel/filter/encode ffmpeg-arg builder every downstream transcode feature will share, pure and unit-tested, not called by anything yet. Needs install-node.ps1 re-run: NO -- ordinary node auto-update covers this (NodeBuildService/deploy.ps1), nothing about first-time provisioning changed. Node/NodeUpdater bumped to 0.110.0 in lockstep.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 110 AND Patch = 0");
        }
    }
}
