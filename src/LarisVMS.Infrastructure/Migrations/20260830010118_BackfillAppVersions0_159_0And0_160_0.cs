using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillAppVersions0_159_0And0_160_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 0.159.0 never got a dedicated BumpVersion migration at all (only the schema-only
            // AddCameraServerMotionEnabled migration shipped alongside it), and 0.160.0's own
            // BumpVersion0_160_0 migration was scaffolded but its Up()/Down() were left empty — both
            // already applied to production with no AppVersions row landing. Guarded with NOT EXISTS
            // rather than a bare INSERT: this migration may run against a database that was set up
            // fresh (via EnsureCreated/full migration replay) after this fix landed, in which case
            // BumpVersion0_160_0's own Up() — never edited, still empty for a truly fresh install —
            // still won't have inserted these rows either, but a database that somehow already has
            // them (e.g. a manual fix) must not get a duplicate.
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM AppVersions WHERE Major = 0 AND Minor = 159 AND Patch = 0)
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 159, 0, '2026-08-29T00:00:00', 'Pass 0 of the detection/hardware-acceleration overhaul: server-side motion detection (MotionSession) now decodes via the same NVDEC/CUDA path AI detection already uses on nodes with an Nvidia accelerator, instead of a continuous software decode — the single largest CPU cost in the whole detection stack. New per-camera ''Run server-side pixel motion detection'' toggle (default on, no behavior change on upgrade).')");

            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM AppVersions WHERE Major = 0 AND Minor = 160 AND Patch = 0)
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 160, 0, '2026-08-29T00:00:00', 'Pass 1 of the detection/hardware-acceleration overhaul: AI detection now fits each camera''s own real aspect ratio into D-FINE''s square input (new Letterbox default, or Stretch) instead of decoding every camera to one fixed global resolution and stretching it into a square regardless of shape. SKBitmap.Resize removed from the AI-detection hot path. Retired the global Detection.Width/Height settings in favor of per-camera decode resolution. Live-detection overlay boxes now line up correctly on non-16:9 cameras.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 159 AND Patch = 0");
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 160 AND Patch = 0");
        }
    }
}
