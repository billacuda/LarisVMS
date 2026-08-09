namespace NidusVMS.Core.Entities;

/// <summary>
/// Global application configuration, dotted-namespace keys (e.g. "Retention.Days",
/// "Recording.PreRecordSeconds"). This table holds the compiled-in-default-overriding *global*
/// value; per-camera/per-group overrides live in <see cref="SettingOverride"/> and are resolved on
/// top of this through ISettingsResolver.
/// </summary>
public class Setting
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystemSetting { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastModifiedAt { get; set; }
    public string? LastModifiedBy { get; set; }
}
