using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class DatabaseMaintenanceTests
{
    [Theory]
    [InlineData(99, 24000, DatabaseMaintenanceService.IndexAction.Rebuild)]
    [InlineData(12, 24000, DatabaseMaintenanceService.IndexAction.Reorganize)]
    [InlineData(3, 24000, DatabaseMaintenanceService.IndexAction.None)]
    [InlineData(99, 500, DatabaseMaintenanceService.IndexAction.None)]   // too small to matter
    public void WhatToDoWithAnIndex(double fragmentation, long pages, DatabaseMaintenanceService.IndexAction expected) =>
        Assert.Equal(expected, DatabaseMaintenanceService.Decide(fragmentation, pages));

    [Fact]
    public void BusyClusteredIndexesRebuildWithRoomToGrow()
    {
        Assert.Equal("ALTER INDEX [IX_Segments_CameraId_StartUtc] ON [dbo].[Segments] REBUILD WITH (ONLINE = ON, FILLFACTOR = 90)",
            DatabaseMaintenanceService.Statement("dbo", "Segments", "IX_Segments_CameraId_StartUtc", clustered: true,
                DatabaseMaintenanceService.IndexAction.Rebuild, online: true));
        Assert.Equal("ALTER INDEX [IX_Segments_FilePath] ON [dbo].[Segments] REBUILD",
            DatabaseMaintenanceService.Statement("dbo", "Segments", "IX_Segments_FilePath", clustered: false,
                DatabaseMaintenanceService.IndexAction.Rebuild, online: false));
        Assert.Equal("ALTER INDEX [a]]b] ON [dbo].[T] REORGANIZE",
            DatabaseMaintenanceService.Statement("dbo", "T", "a]b", clustered: true, DatabaseMaintenanceService.IndexAction.Reorganize, online: true));
    }

    [Fact]
    public void RunsAtThreeInTheMorning()
    {
        Assert.Equal(TimeSpan.FromHours(1), DatabaseMaintenanceService.UntilNextRun(new DateTime(2026, 10, 19, 2, 0, 0)));
        Assert.Equal(TimeSpan.FromHours(23), DatabaseMaintenanceService.UntilNextRun(new DateTime(2026, 10, 19, 4, 0, 0)));
    }
}
