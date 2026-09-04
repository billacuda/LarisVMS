using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_180_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 180, 0, GETUTCDATE(), 'Live view can now label each AI detection box with its confidence. A new Confidence switch in the view toolbar appends each box''s score as a percentage to its label. It stays greyed out while both Moving and Idle are off, since there are no boxes for it to annotate, and its own setting is remembered independently - turning the boxes back on restores your confidence choice with them. The Moving and Idle detection controls are now switches rather than checkboxes, matching the new control beside them. All three are off by default and saved to your account, so they survive a refresh and follow you to another browser or device. Web-only change: no recorder node rebuild or install-node.ps1 re-run.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 180 AND Patch = 0");
        }
    }
}
