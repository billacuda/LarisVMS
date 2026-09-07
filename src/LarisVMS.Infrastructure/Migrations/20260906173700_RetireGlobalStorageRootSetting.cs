using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RetireGlobalStorageRootSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Storage config is now per-node (Nodes.StorageRootPath / ArchiveRootPath) with no global
            // default. Copy the old global Storage.RootPath into any node still inheriting it BEFORE
            // deleting the setting, so recording on those nodes continues unbroken. Archive.RootPath
            // never shipped as a released global, but delete it defensively in case an intermediate
            // build wrote one.
            migrationBuilder.Sql(@"
                UPDATE Nodes
                SET StorageRootPath = (SELECT TOP 1 Value FROM Settings WHERE [Key] = 'Storage.RootPath')
                WHERE (StorageRootPath IS NULL OR LTRIM(RTRIM(StorageRootPath)) = '')
                  AND EXISTS (SELECT 1 FROM Settings WHERE [Key] = 'Storage.RootPath'
                              AND Value IS NOT NULL AND LTRIM(RTRIM(Value)) <> '');");
            migrationBuilder.Sql("DELETE FROM Settings WHERE [Key] IN ('Storage.RootPath', 'Archive.RootPath');");
            migrationBuilder.Sql("DELETE FROM SettingOverrides WHERE [Key] IN ('Storage.RootPath', 'Archive.RootPath');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Best-effort: reinstate a global Storage.RootPath from whatever the first node with a
            // path has, so a rollback isn't left with no default at all. Per-node paths are kept
            // (they're columns, untouched here) — this only restores the retired setting row.
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM Settings WHERE [Key] = 'Storage.RootPath')
                    INSERT INTO Settings (Id, [Key], [Value], IsSystemSetting, CreatedAt)
                    SELECT NEWID(), 'Storage.RootPath',
                           (SELECT TOP 1 StorageRootPath FROM Nodes
                            WHERE StorageRootPath IS NOT NULL AND LTRIM(RTRIM(StorageRootPath)) <> ''
                            ORDER BY CreatedAt),
                           0, GETUTCDATE()
                    WHERE EXISTS (SELECT 1 FROM Nodes
                                  WHERE StorageRootPath IS NOT NULL AND LTRIM(RTRIM(StorageRootPath)) <> '');");
        }
    }
}
