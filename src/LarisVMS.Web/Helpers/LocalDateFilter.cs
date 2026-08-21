namespace LarisVMS.Web.Helpers;

/// <summary>
/// Converts a day-granularity date picked in a filter form (an <c>&lt;input type="date"&gt;</c>, which
/// posts a bare "yyyy-MM-dd" with no timezone at all) into the UTC instants that bound that day *in
/// the viewer's own reckoning*, for comparison against the UTC columns every table in this app stores.
///
/// Both Snapshots and Audit Logs previously passed the parsed date straight through as a
/// <see cref="DateTimeKind.Unspecified"/> value. Everything downstream treats Unspecified as already-UTC
/// (see TimelineService.NormalizeToUtc, deliberately, since its other callers really do send UTC), so
/// "20 Aug" became 00:00–23:59:59 *UTC* rather than local. Confirmed live at UTC-7: results stopped at
/// 4:59 PM, which is exactly 23:59:59Z, and the last seven hours of the selected day were missing.
///
/// Local means the *server's* timezone, matching how every timestamp in these same tables is already
/// rendered (<c>AtUtc.ToLocalTime()</c>). Getting the browser's zone instead would be more correct for
/// a remote viewer, but it would also disagree with the times printed next to the filter — worse than
/// the inconsistency being fixed here. Changing both together is its own pass.
///
/// A DST transition day is 23 or 25 hours long rather than 24; ToUniversalTime handles that per
/// instant, so no arithmetic here assumes a fixed day length.
/// </summary>
public static class LocalDateFilter
{
    /// <summary>The UTC instant at which the given local date begins, or null when the text isn't a
    /// date (including empty — an untouched filter field), which every caller treats as "no bound".</summary>
    public static DateTime? StartOfDayUtc(string? date) =>
        DateOnly.TryParse(date, out var d)
            ? DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime()
            : null;

    /// <summary>The UTC instant at the very end of the given local date (23:59:59.9999999 local), so
    /// an inclusive upper-bound comparison covers the whole day.</summary>
    public static DateTime? EndOfDayUtc(string? date) =>
        DateOnly.TryParse(date, out var d)
            ? DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Local).ToUniversalTime()
            : null;
}
