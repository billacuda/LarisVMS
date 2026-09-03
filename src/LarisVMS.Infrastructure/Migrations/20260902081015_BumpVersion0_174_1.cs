using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_174_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 174, 1, GETUTCDATE(), 'The AI detection loop no longer allocates two large buffers per frame. Each processed frame copied itself into a freshly allocated 1.56 MB buffer, and YOLOX copied its whole output tensor out again before decoding it (2.86 MB) - both large enough to land on the .NET Large Object Heap, so a six-camera recorder produced on the order of 200 MB/sec of large-object garbage just moving data it already had. The frame now reuses one buffer for the life of the pipeline and the decoder reads the output tensor in place; neither changes what the model sees. The detection cadence log line also now reports process-wide gen2 collection count and allocation rate, to tell a garbage-collection stall apart from GPU contention.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 174 AND Patch = 1");
        }
    }
}
