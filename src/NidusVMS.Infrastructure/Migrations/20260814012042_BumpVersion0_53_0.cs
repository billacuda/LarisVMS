using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_53_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 53, 0, GETUTCDATE(), 'Fixed: install-node.ps1 (and every other first-party .ps1 script) had no UTF-8 BOM, so Windows PowerShell 5.1 -- unlike PowerShell 7, whose parser was used to verify the file earlier and found nothing wrong -- fell back to decoding it with the system ANSI codepage instead of UTF-8. That mangled the em dashes and curly apostrophes used throughout this codebase''s comments into byte sequences that broke the tokenizer, producing Missing closing )/} parse errors confirmed live on nvr1. All four scripts now carry a UTF-8 BOM, which both PowerShell versions honor correctly.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 53 AND Patch = 0");
        }
    }
}
