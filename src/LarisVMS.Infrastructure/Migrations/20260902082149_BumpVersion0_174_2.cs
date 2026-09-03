using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_174_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 174, 2, GETUTCDATE(), 'AI detection no longer allocates its model input buffer per frame. 0.174.1 removed two large per-frame allocations; instrumentation on a live six-camera recorder then showed the process still allocating 140-255 MB/sec and running 4-7 gen2 garbage collections per second, essentially all of it one line - the input tensor handed to the model, freshly allocated at 4.92 MB every inference and large enough to land on the Large Object Heap every time. It is now built once per camera and refilled. D-FINE keeps two such buffers rather than one, because its high-resolution re-detection runs on a separate task alongside the continuous loop and the two must not share.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 174 AND Patch = 2");
        }
    }
}
