using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_86_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 86, 1, GETUTCDATE(), 'Fixed: probe-metadata-track.ps1 reported a false positive -- it accepted a match on MetadataStream/VideoAnalytics (present in any ONVIF metadata stream) as proof that bounding boxes were feasible, and did exactly that against a real camera whose payload had no object geometry at all. It now checks the whole capture for the elements that actually carry geometry, tells a motion-cell-only stream apart from a real object stream, and prints an element census. Research: the metadata track exists and ffmpeg demuxes it cleanly, but this camera family sends a 22x18 MotionInCells grid, not object rectangles -- bounding boxes are blocked on camera hardware, not on this codebase. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 86 AND Patch = 1");
        }
    }
}
