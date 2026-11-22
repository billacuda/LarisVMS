using LarisVMS.Core.Localization;

namespace LarisVMS.Web.Helpers;

/// <summary>
/// What to show for an exception caught on a page. LarisVMS's own refusals (ArgumentException,
/// InvalidOperationException, FormatException) carry messages written for people and are shown as
/// they are; anything else (a database error, a bug) gets a general message, with the details in the
/// server's log rather than on the page.
/// </summary>
public static class UserFacingError
{
    /// <summary>Set at startup; null (tests) skips logging.</summary>
    public static ILogger? Logger { get; set; }

    public static string Message(Exception ex)
    {
        // Only refusals LarisVMS itself threw: EF Core and the framework throw these same types (an
        // InvalidOperationException's type is always in System), with messages about internals.
        if (ex is ArgumentException or InvalidOperationException or FormatException && ThrownByLarisVms(ex))
            return ex.Message;
        Logger?.LogError(ex, "Unexpected error on a page.");
        return Loc.T("common.unexpectedError");
    }

    /// <summary>Whether the method that threw is LarisVMS code (async methods included: their state
    /// machine is nested in the LarisVMS type).</summary>
    internal static bool ThrownByLarisVms(Exception ex) =>
        ex.TargetSite?.DeclaringType?.Namespace?.StartsWith("LarisVMS", StringComparison.Ordinal) == true;
}
