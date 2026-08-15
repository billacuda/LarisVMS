using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>
/// M11: tails FileLoggerProvider's own daily-rolling files from ".\logs" — the Web tier's own
/// structured application log, distinct from the Admin > Audit Log page (who did what) and from
/// LarisVMS.Node's own logs (a separate machine's local disk this page has no access to; an admin
/// reads those directly from %ProgramData%\LarisVMS\logs on the recorder itself for now — see the
/// M7/M8/M11 finish-up plan's backlog for a remote node-log viewer). Reads the file directly rather
/// than duplicating lines into the database — it's already on disk, and this app's log volume
/// doesn't call for a second copy.
/// </summary>
[Authorize("SystemLogs.View")]
public class SystemLogsModel : PageModel
{
    private const int MaxLines = 1000;

    public List<string> AvailableDates { get; set; } = [];
    public string? SelectedDate { get; set; }
    public string? Query { get; set; }
    public List<string> Lines { get; set; } = [];
    public bool Truncated { get; set; }
    public string? ErrorMessage { get; set; }

    private static string LogsDir => Path.Combine(AppContext.BaseDirectory, "logs");

    public void OnGet(string? date, string? q)
    {
        Query = q;

        if (!Directory.Exists(LogsDir))
        {
            ErrorMessage = "No log files yet.";
            return;
        }

        AvailableDates = Directory.EnumerateFiles(LogsDir, "app-*.log")
            .Select(p => Path.GetFileNameWithoutExtension(p).Replace("app-", ""))
            .OrderDescending()
            .ToList();

        if (AvailableDates.Count == 0)
        {
            ErrorMessage = "No log files yet.";
            return;
        }

        SelectedDate = date is not null && AvailableDates.Contains(date) ? date : AvailableDates[0];
        var path = Path.Combine(LogsDir, $"app-{SelectedDate}.log");

        List<string> allLines;
        try
        {
            // FileShare.ReadWrite on the writer side (FileLoggerProvider) is what makes reading
            // today's still-being-appended file safe to do concurrently.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            allLines = [];
            while (reader.ReadLine() is { } line) allLines.Add(line);
        }
        catch (IOException ex)
        {
            ErrorMessage = $"Could not read {path}: {ex.Message}";
            return;
        }

        if (!string.IsNullOrWhiteSpace(q))
            allLines = allLines.Where(l => l.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        Truncated = allLines.Count > MaxLines;
        Lines = allLines.Count > MaxLines ? allLines[^MaxLines..] : allLines;
    }
}
