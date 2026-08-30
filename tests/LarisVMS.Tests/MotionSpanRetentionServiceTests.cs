using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>
/// Pass 2c: "a card that can't be played back is noise" — a MotionSpan is stale once its camera's
/// earliest remaining Segment starts after the span's own PlayFromUtc (StartUtc minus the camera's
/// pre-roll, not StartUtc itself — see MotionSpanRetentionService's own doc comment for why), or
/// immediately if the camera has no segments left at all. Mirrors BookmarkRetentionServiceTests'
/// shape — a real InMemory ApplicationDbContext, not a pure-logic test, since the interesting behavior
/// is entirely in the join/comparison against Segments.
/// </summary>
public class MotionSpanRetentionServiceTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static MotionSpan MakeSpan(Guid cameraId, DateTime startUtc) => new()
    {
        CameraId = cameraId,
        Source = MotionSource.ServerMotion,
        StartUtc = startUtc,
        EndUtc = startUtc.AddSeconds(30),
        Score = 0.5
    };

    private static Segment MakeSegment(Guid cameraId, DateTime startUtc) => new()
    {
        CameraId = cameraId,
        NodeId = Guid.NewGuid(),
        StreamRole = CameraStreamRole.Main,
        StartUtc = startUtc,
        EndUtc = startUtc.AddMinutes(1),
        FilePath = Guid.NewGuid() + ".mp4"
    };

    // No ISettingsResolver in most of these tests — resolves to the same 10s default
    // TimelineService.GetSnapshotsAsync itself falls back to when none is supplied.
    private const int DefaultPreRollSeconds = 10;

    private sealed class StubSettingsResolver(Dictionary<Guid, int> preRollByCameraId) : ISettingsResolver
    {
        public Task<string?> GetRawAsync(string key, Guid? cameraId = null, Guid? nodeId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<T> GetAsync<T>(string key, T defaultValue, Guid? cameraId = null, Guid? nodeId = null, CancellationToken ct = default)
        {
            if (typeof(T) == typeof(int) && cameraId is { } camId && preRollByCameraId.TryGetValue(camId, out var seconds))
                return Task.FromResult((T)(object)seconds);
            return Task.FromResult(defaultValue);
        }

        public Task<SettingScope?> GetSourceAsync(string key, Guid? cameraId = null, Guid? nodeId = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string?> GetOwnOverrideAsync(SettingScope scope, Guid scopeId, string key, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetGlobalAsync(string key, string value, string? modifiedBy = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetOverrideAsync(SettingScope scope, Guid scopeId, string key, string? value, string? modifiedBy = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task InvalidateAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task SpanNewerThanEarliestSegmentSurvives()
    {
        using var db = NewDb();
        var cameraId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Segments.Add(MakeSegment(cameraId, now.AddDays(-10)));
        db.MotionSpans.Add(MakeSpan(cameraId, now.AddDays(-5)));
        await db.SaveChangesAsync();

        await MotionSpanRetentionService.SweepAsync(db, null, CancellationToken.None);

        Assert.Equal(1, await db.MotionSpans.CountAsync());
    }

    [Fact]
    public async Task SpanOlderThanEarliestRemainingSegmentIsDeleted()
    {
        using var db = NewDb();
        var cameraId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        // Footage has already retention-aged past the span's own instant.
        db.Segments.Add(MakeSegment(cameraId, now.AddDays(-5)));
        db.MotionSpans.Add(MakeSpan(cameraId, now.AddDays(-10)));
        await db.SaveChangesAsync();

        await MotionSpanRetentionService.SweepAsync(db, null, CancellationToken.None);

        Assert.Equal(0, await db.MotionSpans.CountAsync());
    }

    [Fact]
    public async Task SpanForCameraWithNoSegmentsAtAllIsDeleted()
    {
        using var db = NewDb();
        var cameraId = Guid.NewGuid();
        db.MotionSpans.Add(MakeSpan(cameraId, DateTime.UtcNow.AddDays(-1)));
        await db.SaveChangesAsync();

        await MotionSpanRetentionService.SweepAsync(db, null, CancellationToken.None);

        Assert.Equal(0, await db.MotionSpans.CountAsync());
    }

    [Fact]
    public async Task OnlyTheStaleSpanIsRemovedWhenMultipleCamerasAreMixed()
    {
        using var db = NewDb();
        var freshCamera = Guid.NewGuid();
        var staleCamera = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Segments.Add(MakeSegment(freshCamera, now.AddDays(-30)));
        // staleCamera has no segments at all — its span should go.
        db.MotionSpans.AddRange(
            MakeSpan(freshCamera, now.AddDays(-1)),
            MakeSpan(staleCamera, now.AddDays(-1)));
        await db.SaveChangesAsync();

        await MotionSpanRetentionService.SweepAsync(db, null, CancellationToken.None);

        var remaining = await db.MotionSpans.ToListAsync();
        Assert.Single(remaining);
        Assert.Equal(freshCamera, remaining[0].CameraId);
    }

    [Fact]
    public async Task NoSpansIsANoOp()
    {
        using var db = NewDb();

        await MotionSpanRetentionService.SweepAsync(db, null, CancellationToken.None);

        Assert.Equal(0, await db.MotionSpans.CountAsync());
    }

    [Fact]
    public async Task ASpanCoveredByStartUtcButNotByPlayFromUtcIsSwept()
    {
        // The pass 2c-specific case: StartUtc itself is still covered by the earliest remaining
        // segment, but StartUtc minus the pre-roll (PlayFromUtc) reaches back past it — the card would
        // promise a lead-in the footage no longer has, so it must still be swept.
        using var db = NewDb();
        var cameraId = Guid.NewGuid();
        var earliestSegmentStart = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.Segments.Add(MakeSegment(cameraId, earliestSegmentStart));
        // StartUtc is 3s after the earliest segment (covered), but the default 10s pre-roll reaches
        // back to 7s *before* it.
        db.MotionSpans.Add(MakeSpan(cameraId, earliestSegmentStart.AddSeconds(3)));
        await db.SaveChangesAsync();

        await MotionSpanRetentionService.SweepAsync(db, null, CancellationToken.None);

        Assert.Equal(0, await db.MotionSpans.CountAsync());
    }

    [Fact]
    public async Task SweepRespectsACameraLevelPreRollOverride()
    {
        using var db = NewDb();
        var cameraId = Guid.NewGuid();
        var earliestSegmentStart = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.Segments.Add(MakeSegment(cameraId, earliestSegmentStart));
        // With only a 1s pre-roll override, StartUtc + 3s - 1s = still after the earliest segment —
        // covered, unlike the default-10s case above.
        db.MotionSpans.Add(MakeSpan(cameraId, earliestSegmentStart.AddSeconds(3)));
        await db.SaveChangesAsync();

        var settings = new StubSettingsResolver(new Dictionary<Guid, int> { [cameraId] = 1 });
        await MotionSpanRetentionService.SweepAsync(db, settings, CancellationToken.None);

        Assert.Equal(1, await db.MotionSpans.CountAsync());
    }

    [Fact]
    public async Task SweepHandlesMoreThanOneBatchWorthOfStaleRowsWithoutLosingOrStallingOnSurvivors()
    {
        using var db = NewDb();
        var freshCamera = Guid.NewGuid();
        var staleCamera = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Segments.Add(MakeSegment(freshCamera, now.AddDays(-30)));

        // A handful of survivors land in the very first batch (lowest Ids), followed by more stale
        // rows than one batch can hold — proves keyset pagination advances past the batch that mixed
        // survivors and deletions, rather than looping forever re-fetching the same page.
        var spans = new List<MotionSpan>();
        for (var i = 0; i < 3; i++) spans.Add(MakeSpan(freshCamera, now.AddDays(-1).AddSeconds(i)));
        for (var i = 0; i < MotionSpanRetentionService.BatchSize + 500; i++) spans.Add(MakeSpan(staleCamera, now.AddDays(-1).AddSeconds(i)));
        db.MotionSpans.AddRange(spans);
        await db.SaveChangesAsync();

        await MotionSpanRetentionService.SweepAsync(db, null, CancellationToken.None);

        var remaining = await db.MotionSpans.ToListAsync();
        Assert.Equal(3, remaining.Count);
        Assert.All(remaining, s => Assert.Equal(freshCamera, s.CameraId));
    }
}
