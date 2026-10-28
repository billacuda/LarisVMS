namespace LarisVMS.Core.Entities;

/// <summary>
/// One row per shipped version, written at startup from LarisVMS.Infrastructure.AppRelease (up to
/// 0.218.0, by a data-only BumpVersionX_Y_Z migration per release). The footer reads the highest row
/// here, so what it shows is the newest version that has started against this database.
/// </summary>
public class AppVersion
{
    public int Id { get; set; }
    public int Major { get; set; }
    public int Minor { get; set; }
    public int Patch { get; set; }
    public DateTime ReleasedAt { get; set; }
    public string? Notes { get; set; }

    public string ToVersionString() => $"{Major}.{Minor}.{Patch}";
}
