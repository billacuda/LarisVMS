using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_83_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 83, 0, GETUTCDATE(), 'Added: multi-sensor camera support (Axis quad-lens and similar). ONVIF probing now reads each profile''s VideoSourceToken -- the only field distinguishing one lens from another -- and a camera whose probe finds more than one sensor offers to split into one camera per lens. Each becomes an ordinary independent camera sharing the device''s address/credentials, so recording, views, live, playback and export needed no changes. Fixes a real pre-existing bug: on a multi-lens device the profile ranker, which knows nothing about channels, could pick another lens''s stream as a camera''s Main. Splitting is safe to re-run (matches existing rows by channel rather than duplicating), and single-lens cameras are unaffected. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 83 AND Patch = 0");
        }
    }
}
