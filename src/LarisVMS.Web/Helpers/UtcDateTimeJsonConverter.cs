using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LarisVMS.Web.Helpers;

/// <summary>Every DateTime this app stores and passes around is UTC, but one that lost its Kind along
/// the way (a value computed in a query, a DTO built by hand) serialized without a trailing "Z" — and a
/// browser parses an offset-less ISO string as its own local time, so the page showed the UTC clock
/// reading as if it were local (a 3 PM event showing as 10 PM in Pacific). Registered for every JSON
/// API response, this always writes UTC with the "Z", and reads an offset-less value as UTC too.</summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetDateTime();
        return value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value,
        };
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value,
        };
        writer.WriteStringValue(utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }
}
