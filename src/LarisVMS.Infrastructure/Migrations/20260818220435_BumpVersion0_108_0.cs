using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_108_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 108, 0, GETUTCDATE(), 'M15 pass 4: alerting. New AlertRule/AlertDelivery entities (Admin -> Alerts) -- a rule watches one camera or node for a condition (not reporting, offline, storage below a percent threshold) and fires through up to six delivery channels: email, webhook, ntfy, Pushover, Slack, Teams. AlertEvaluatorService (BackgroundService, 1-minute tick) reuses DashboardService''s own freshness/online windows so an alert agrees with what the Dashboard already shows. Per-rule cooldown avoids re-alerting every tick; deliberately no resolved notification when a condition clears. Delivery config (webhook/Slack/Teams URLs, Pushover tokens, ntfy topic) is encrypted at rest like every other integration secret. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 108 AND Patch = 0");
        }
    }
}
