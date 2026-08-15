using LarisVMS.Core.Logging;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Tests;

/// <summary>Covers FileLoggerProvider against a real temp directory — the actual file write/rollover
/// behavior is the whole point of this class, so a real filesystem (not a mock) is what's worth
/// testing here.</summary>
public class FileLoggerProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "larisvms-logtest-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void WritesAnInformationLineToTodaysFile()
    {
        using var provider = new FileLoggerProvider(_dir, "app", LogLevel.Information);
        var logger = provider.CreateLogger("TestCategory");

        logger.LogInformation("hello world");
        provider.Dispose();

        var expectedPath = Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log");
        var content = File.ReadAllText(expectedPath);
        Assert.Contains("hello world", content);
        Assert.Contains("[INFO]", content);
        Assert.Contains("TestCategory", content);
    }

    [Fact]
    public void SuppressesLinesBelowTheMinimumLevel()
    {
        using var provider = new FileLoggerProvider(_dir, "app", LogLevel.Warning);
        var logger = provider.CreateLogger("TestCategory");

        logger.LogInformation("should not appear");
        logger.LogWarning("should appear");
        provider.Dispose();

        var expectedPath = Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log");
        var content = File.ReadAllText(expectedPath);
        Assert.DoesNotContain("should not appear", content);
        Assert.Contains("should appear", content);
    }

    [Fact]
    public void IncludesTheExceptionWhenOneIsLogged()
    {
        using var provider = new FileLoggerProvider(_dir, "app", LogLevel.Information);
        var logger = provider.CreateLogger("TestCategory");

        logger.LogError(new InvalidOperationException("boom"), "something failed");
        provider.Dispose();

        var expectedPath = Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log");
        var content = File.ReadAllText(expectedPath);
        Assert.Contains("something failed", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("boom", content);
    }

    [Fact]
    public void DoesNotCreateTheDirectoryOrFileWhenNothingIsLogged()
    {
        using var provider = new FileLoggerProvider(_dir, "app", LogLevel.Information);
        provider.CreateLogger("Unused");

        Assert.False(Directory.Exists(_dir));
    }
}
