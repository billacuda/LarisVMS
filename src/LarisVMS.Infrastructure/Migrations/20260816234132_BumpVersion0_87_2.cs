using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_87_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 87, 2, GETUTCDATE(), 'Added: probe-dahua-events.ps1 -UsePluginCodes, which subscribes with the exact filtered code list the plugin sends instead of [All]. Probing with [All] proves which codes a camera can emit, but the plugin asks for a narrow codes list -- firmware that mishandles a long filter would go silent in production while an [All] probe still looked perfect. Verified against an Amcrest IP8M-DLB2998EW-AI: SmartMotionHuman arrives as clean Start/Stop pairs and maps to Person. Script only; no application change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 87 AND Patch = 2");
        }
    }
}
