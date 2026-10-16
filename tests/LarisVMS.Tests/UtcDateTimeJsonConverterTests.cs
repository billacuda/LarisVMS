using System.Text.Json;
using LarisVMS.Web.Helpers;

namespace LarisVMS.Tests;

/// <summary>API timestamps always reach the browser as UTC with a "Z" — an offset-less one is read
/// by the browser as local time, which put UTC clock readings on screen as if they were local.</summary>
public class UtcDateTimeJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new UtcDateTimeJsonConverter() } };

    [Fact]
    public void UnspecifiedAndUtcTimesBothGoOutWithAZ()
    {
        var at = new DateTime(2026, 10, 13, 15, 0, 0);
        Assert.Equal("\"2026-10-13T15:00:00Z\"", JsonSerializer.Serialize(DateTime.SpecifyKind(at, DateTimeKind.Unspecified), Options));
        Assert.Equal("\"2026-10-13T15:00:00Z\"", JsonSerializer.Serialize(DateTime.SpecifyKind(at, DateTimeKind.Utc), Options));
        Assert.Equal("{\"at\":\"2026-10-13T15:00:00.5Z\"}", JsonSerializer.Serialize(new { At = (DateTime?)at.AddMilliseconds(500) }, Options));
    }

    [Fact]
    public void OffsetLessInputIsReadAsUtc()
    {
        var value = JsonSerializer.Deserialize<DateTime>("\"2026-10-13T15:00:00\"", Options);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(15, value.Hour);
    }
}
