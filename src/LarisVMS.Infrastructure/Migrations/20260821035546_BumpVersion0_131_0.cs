using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_131_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 131, 0, GETUTCDATE(), 'Added: Dahua/Amcrest detections now classify from the event payload, not just its code. An IVS rule fires one code (CrossRegionDetection for intrusion, CrossLineDetection for a tripwire) for every class it matches, so the code alone could only say ''an object'' -- a rule set to Human and Vehicle showed both as a generic Object. The class is in the payload as Objects[].ObjectType, which was previously ignored; the parser now reads it (Human -> Person, Vehicle -> Vehicle, HumanFace -> Face, Animal/Pet -> Animal) and prefers it over the code mapping, falling back to the old behavior when a payload carries no object detail. The session accumulates the pretty-printed multi-line JSON until braces balance, capped at 64 KB. Install-node.ps1 re-run needed -- Node/NodeUpdater bumped to 0.131.0.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 131 AND Patch = 0");
        }
    }
}
