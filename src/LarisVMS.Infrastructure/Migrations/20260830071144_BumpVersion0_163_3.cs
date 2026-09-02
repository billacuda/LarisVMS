using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_163_3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 163, 3, GETUTCDATE(), '0.163.2s own debug image dump silently failed to write anything with no indication why - the exact mistake 0.163.1 had just fixed for Vision Service generally, repeated locally: the failure was logged at Debug, below Vision Services own Information minimum. Bumped to Warning, along with the main-frame-buffer-unreachable failure path in the same method. Also: live confirmation that pass 3bs whole-frame pass correctly identified a real moving car and, earlier, a parked fifth-wheel trailer (reported as bus) - what looked like an implausible pile of detections was very likely a busy real scene, not corrupted decode.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 163 AND Patch = 3");
        }
    }
}
