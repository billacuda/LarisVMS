namespace LarisVMS.Core;

/// <summary>
/// File-name helpers for a path that came from another machine — a node's recording or export path,
/// stored by the server and shown or packaged by it. <see cref="Path"/> follows the server's own OS:
/// on a Linux server <c>Path.GetFileName(@"D:\exports\clip.mp4")</c> returns the whole string, because
/// '\' isn't a separator there. A mixed fleet has Windows paths on a Linux server and the reverse, so
/// these treat both '\' and '/' as separators whatever the server runs on.
/// </summary>
public static class ForeignPath
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>The last segment of a Windows or Linux path ("clip.mp4").</summary>
    public static string GetFileName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var cut = path.LastIndexOfAny(Separators);
        return cut < 0 ? path : path[(cut + 1)..];
    }

    /// <summary><see cref="GetFileName"/> without its last extension ("clip").</summary>
    public static string GetFileNameWithoutExtension(string path)
    {
        var name = GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }
}
