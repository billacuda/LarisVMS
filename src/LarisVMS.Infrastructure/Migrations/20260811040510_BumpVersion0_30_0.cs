using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_30_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 30, 0, GETUTCDATE(), 'Three real reports from live testing, investigated with DB+filesystem access: (1) timeline drag could get stuck to the cursor if the mouse was released outside the browser window - fixed with Pointer Events + setPointerCapture; (2) Cameras/Edit had no link to Zones once a zone already existed - added one; (3) motion playback could report a recording missing on the recorder - confirmed ~1/3 of Segments rows across both nodes point at files no longer on disk, mostly legacy debt from before the v0.27.0 deletion-report retry fix reached these nodes. StorageManager now reconciles its Segments rows against disk hourly and reports genuinely-missing ones for cleanup, self-healing instead of a one-off DB cleanup. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 30 AND Patch = 0");
        }
    }
}
