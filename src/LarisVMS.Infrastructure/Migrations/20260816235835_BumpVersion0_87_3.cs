using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_87_3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 87, 3, GETUTCDATE(), 'Fixed: a dropped smart-event connection could leave a detection span open forever. Dahua attach only delivers events from the moment it subscribes, so a Stop sent while the feed was down is gone for good, and the checkpoint loop kept extending the open span indefinitely -- a camera reboot while someone was in frame read as a person standing there for hours. Open spans now close at the point the feed died; a fresh Start after reconnect opens a new span. Node change -- install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 87 AND Patch = 3");
        }
    }
}
