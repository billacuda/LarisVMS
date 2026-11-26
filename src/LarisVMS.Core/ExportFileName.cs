using System.Text.RegularExpressions;

namespace LarisVMS.Core;

/// <summary>
/// An export's file on the node carries its item's ID, so two exports of the same camera and time never
/// share a file: one of them may have areas blacked out. People never see it: downloads, the package,
/// and the manifest use <see cref="Friendly"/>.
/// </summary>
public static partial class ExportFileName
{
    /// <summary>"clip.mp4" -&gt; "clip_{id}.mp4".</summary>
    public static string WithItemId(string fileName, Guid itemId) =>
        $"{Path.GetFileNameWithoutExtension(fileName)}_{itemId:N}{Path.GetExtension(fileName)}";

    /// <summary>The name to show for a node's export file, given its path there: without the ID. One made
    /// before names had an ID is returned as it is.</summary>
    public static string Friendly(string path) => ItemId().Replace(ForeignPath.GetFileName(path), "");

    // Before ".mp4", or ".h264.mp4" for the standalone player's copy.
    [GeneratedRegex(@"_[0-9a-f]{32}(?=(\.h264)?\.mp4$)", RegexOptions.CultureInvariant)]
    private static partial Regex ItemId();
}
