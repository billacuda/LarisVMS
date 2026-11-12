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
        if (ex is ArgumentException or InvalidOperationException or FormatException
            && ex.GetType().Namespace?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) != true)
            return ex.Message;
        Logger?.LogError(ex, "Unexpected error on a page.");
        return Loc.T("common.unexpectedError");
    }
}
