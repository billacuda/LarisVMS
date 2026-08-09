using System.Runtime.InteropServices;

namespace NidusVMS.Node;

public static class DiskSpace
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetDiskFreeSpaceEx(string lpDirectoryName,
        out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    /// <summary>Free/total bytes for the volume or SMB share containing <paramref name="path"/>.
    /// Uses GetDiskFreeSpaceEx directly rather than System.IO.DriveInfo, which doesn't resolve UNC
    /// paths — this works identically for a local storage root and a \\server\share one. Returns
    /// null if the path doesn't exist yet or isn't reachable (e.g. the SMB share is down); callers
    /// treat that as "stats unknown for now", not fatal.</summary>
    public static (long FreeBytes, long TotalBytes)? TryGetUsage(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return null;
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
