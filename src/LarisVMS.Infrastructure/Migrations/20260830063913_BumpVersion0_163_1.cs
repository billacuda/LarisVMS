using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_163_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 163, 1, GETUTCDATE(), 'LarisVMS.Vision.Service never had its own log file. Its console output is captured by VisionServiceSupervisor.DrainOutputAsync and re-logged into Node''s own logger at Debug severity regardless of the real level, while Node''s file logger has an Information minimum, so every line Vision Service produced (including pass 3b''s own high-res re-detection diagnostics) was silently dropped before reaching any log file. Vision Service now has its own FileLoggerProvider (vision- prefix, Information minimum) writing into the same shared logs directory as Node''s own log, and StorageManager''s 14-day log retention sweep now covers vision-*.log files too.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 163 AND Patch = 1");
        }
    }
}
