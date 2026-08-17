using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_88_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 88, 1, GETUTCDATE(), 'Changed: the recorder''s live fan-out no longer allocates when nobody is watching. ffmpeg stdout is read through System.IO.Pipelines instead of Stream.ReadAsync into our own array, so fragment boundaries are found in the pipe''s pooled buffers and nothing is copied unless a fragment is actually handed to a viewer. Measured over one camera-hour: with a viewer, 18% less CPU; with no viewer, 83% less CPU and allocation down from 4.1 GB to zero. Box types are now compared as integers rather than decoded to a string per box. Node change -- install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 88 AND Patch = 1");
        }
    }
}
