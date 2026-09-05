using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_186_3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 186, 3, GETUTCDATE(), 'Changed: the vision log now identifies a camera by its name rather than its GUID. Every line was prefixed with 36 characters of identifier, and the detection cadence, slice layout and engine build messages repeated it in the body. They now read Vision[Driveway]. The full camera id is still logged once when a pipeline starts and on every failure, so a line can always be tied back to a camera row; a camera with no name falls back to the first block of its id. Renaming a camera deliberately does not restart its detection pipeline - that would mean a multi-minute TensorRT engine rebuild for a cosmetic change - so the new name reaches the log on that camera''s next restart for some other reason.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 186 AND Patch = 3");
        }
    }
}
