using LarisVMS.Core.Dtos;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// Folds a snapshot's per-track object rows back into the real objects behind them. ByteTrack can
/// switch IDs while one person walks through, so one walk-through can come back as several track
/// rows. Shown as-is, that gives "black pants ×6" for one person. Two fragments of the same object
/// never overlap in time, but two objects in frame together do. So each label's rows are chained
/// into time-disjoint slots, one per object. Each slot keeps the highest-confidence color for each
/// part and its fastest speed reading. Pure, so it's unit-testable without a database.
/// </summary>
public static class SnapshotObjectCollapser
{
    /// <summary>One DetectedObjects row, as the Snapshots page loads it.</summary>
    public sealed record Row(string Label, string? PrimaryColor, string? UpperColor, string? LowerColor,
        double? SpeedKph, double? Confidence, DateTime FirstSeenUtc, DateTime LastSeenUtc);

    /// <summary>One collapsed object, with the ranking inputs the page sorts on.</summary>
    public sealed record Collapsed(SnapshotObjectDto Object, double Confidence);

    /// <summary>How far a new fragment may overlap the end of the previous one and still continue it.
    /// Covers the tracker reporting the old ID for a frame or two after the new one appears.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromSeconds(0.5);

    public static List<Collapsed> Collapse(IEnumerable<Row> rows)
    {
        var result = new List<Collapsed>();
        foreach (var byLabel in rows.GroupBy(r => r.Label, StringComparer.OrdinalIgnoreCase))
        {
            var slots = new List<List<Row>>();
            foreach (var row in byLabel.OrderBy(r => r.FirstSeenUtc).ThenBy(r => r.LastSeenUtc))
            {
                var free = slots.Where(s => s.Max(r => r.LastSeenUtc) <= row.FirstSeenUtc + Overlap).ToList();
                var slot = free
                    .OrderByDescending(s => ColorMatches(s, row))
                    .ThenByDescending(s => s.Max(r => r.LastSeenUtc))
                    .FirstOrDefault();
                if (slot is null) slots.Add(slot = []);
                slot.Add(row);
            }
            result.AddRange(slots.Select(s => Fold(byLabel.Key, s)));
        }
        return result;
    }

    private static Collapsed Fold(string label, List<Row> slot)
    {
        static string? Best(List<Row> rows, Func<Row, string?> pick) => rows
            .Where(r => pick(r) is not null)
            .OrderByDescending(r => r.Confidence ?? 0)
            .Select(pick)
            .FirstOrDefault();

        var speed = slot.Where(r => r.SpeedKph is not null).Select(r => r.SpeedKph).DefaultIfEmpty(null).Max();
        return new Collapsed(
            new SnapshotObjectDto(label, Best(slot, r => r.PrimaryColor), Best(slot, r => r.UpperColor),
                Best(slot, r => r.LowerColor), speed),
            slot.Max(r => r.Confidence ?? 0));
    }

    /// <summary>How many of the row's colors the slot has already seen, to pick between free slots.</summary>
    private static int ColorMatches(List<Row> slot, Row row)
    {
        static bool Seen(List<Row> s, string? color, Func<Row, string?> pick) =>
            color is not null && s.Any(r => string.Equals(pick(r), color, StringComparison.OrdinalIgnoreCase));
        return (Seen(slot, row.PrimaryColor, r => r.PrimaryColor) ? 1 : 0)
            + (Seen(slot, row.UpperColor, r => r.UpperColor) ? 1 : 0)
            + (Seen(slot, row.LowerColor, r => r.LowerColor) ? 1 : 0);
    }
}
