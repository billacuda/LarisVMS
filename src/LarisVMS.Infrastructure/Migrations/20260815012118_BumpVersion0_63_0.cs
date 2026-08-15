using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_63_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 63, 0, GETUTCDATE(), 'Fixed: hover thumbnails could still render as a broken image even with every node-side fix installed. timeline.js''s cache eviction picked the first-inserted key as oldest, but Map.set on an existing key does not reorder it, so a repeatedly-hovered bucket could be evicted (its blob URL revoked) while still on screen. Cache hits now bump to most-recently-used first. Also added an <img> error handler as a backstop so a bad image always falls back to No preview available instead of a broken-image icon. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 63 AND Patch = 0");
        }
    }
}
