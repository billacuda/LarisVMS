using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_149_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 149, 2, GETUTCDATE(), 'Fixed: the multi-group camera error (A camera can only belong to groups under a single site) had no way to see or set a camera''s site. There is no separate site field - a group''s site is whichever top-level group it descends from. Cameras/Edit''s Groups multi-select now renders one optgroup per site instead of one flat list. Cameras > Groups manage-cameras popup now names each camera''s current site alongside its existing groups. The Groups page intro now says outright that a top-level group is a site. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 149 AND Patch = 2");
        }
    }
}
