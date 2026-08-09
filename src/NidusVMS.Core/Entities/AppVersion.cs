namespace NidusVMS.Core.Entities;

/// <summary>
/// One row per shipped version, inserted by a data-only EF migration (see the
/// "BumpVersionX_Y_Z" convention in the README). The footer reads the highest row here, so what it
/// shows is the version the database is actually migrated to — a half-applied deploy (new binaries,
/// unapplied migrations, or vice versa) is visible on every page.
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
