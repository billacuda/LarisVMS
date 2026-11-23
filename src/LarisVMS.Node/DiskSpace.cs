using System.Runtime.InteropServices;

namespace LarisVMS.Node;

public static class DiskSpace
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceEx(string lpDirectoryName,
        out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    /// <summary>Free/total bytes for the volume or network share containing <paramref name="path"/>.
    /// On Windows this uses GetDiskFreeSpaceEx directly rather than System.IO.DriveInfo, which doesn't
    /// resolve UNC paths — it works identically for a local storage root and a \\server\share one.
    /// Elsewhere DriveInfo takes any path and asks statvfs about the file system holding it, so a
    /// mounted NFS or CIFS share works the same way. Returns null if the path doesn't exist yet or
    /// isn't reachable (e.g. the share is down); callers treat that as "stats unknown for now", not
    /// fatal — but the storage watermark and archiving both need this, so it must work on every OS.</summary>
    public static (long FreeBytes, long TotalBytes)? TryGetUsage(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return null;
            if (!OperatingSystem.IsWindows())
            {
                var drive = new DriveInfo(path);
                return (drive.AvailableFreeSpace, drive.TotalSize);
            }
            return GetDiskFreeSpaceEx(path, out var free, out var total, out _)
                ? ((long)free, (long)total)
                : null;
        }
        catch
        {
            return null;
        }
    }
}
