using System.Text.Json;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Core;

/// <summary>Reads and checks SpeedTrap.GatesJson (<c>{"a":[[x,y],[x,y]],"b":[[x,y],[x,y]]}</c>,
/// normalized 0-1). One parser for the API's validation and NodeService's config build, so a trap
/// the editor saved is always one Vision can use.</summary>
public static class SpeedTrapGates
{
    public sealed record Gates(double AX1, double AY1, double AX2, double AY2,
        double BX1, double BY1, double BX2, double BY2);

    /// <summary>Gate lines shorter than this (in normalized units) are a mis-click, not a gate.</summary>
    private const double MinGateLength = 0.02;

    public static Gates? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (ReadLine(doc.RootElement, "a") is not { } a || ReadLine(doc.RootElement, "b") is not { } b) return null;
            return new Gates(a.X1, a.Y1, a.X2, a.Y2, b.X1, b.Y1, b.X2, b.Y2);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Null when valid, otherwise what's wrong, for the editor to show.</summary>
    public static string? Validate(string? json, double distanceMeters)
    {
        if (Parse(json) is null) return "Draw both gate lines (A and B) across the road.";
        if (distanceMeters is <= 0 or > 1000) return "Enter the distance between the gates (more than 0, up to 1000 m).";
        return null;
    }

    public static VisionSpeedTrapDto? ToVision(Guid id, string? json, double distanceMeters)
    {
        if (Parse(json) is not { } g || distanceMeters <= 0) return null;
        return new VisionSpeedTrapDto(id, g.AX1, g.AY1, g.AX2, g.AY2, g.BX1, g.BY1, g.BX2, g.BY2, distanceMeters);
    }

    private static (double X1, double Y1, double X2, double Y2)? ReadLine(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var line)
            || line.ValueKind != JsonValueKind.Array || line.GetArrayLength() != 2) return null;
        var p = new double[4];
        var i = 0;
        foreach (var point in line.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2) return null;
            foreach (var v in point.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Number) return null;
                var d = v.GetDouble();
                if (!double.IsFinite(d) || d < 0 || d > 1) return null;
                p[i++] = d;
            }
        }
        var len = Math.Sqrt((p[2] - p[0]) * (p[2] - p[0]) + (p[3] - p[1]) * (p[3] - p[1]));
        return len < MinGateLength ? null : (p[0], p[1], p[2], p[3]);
    }
}
