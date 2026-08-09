using NidusVMS.Core.Enums;

namespace NidusVMS.Core.Entities;

/// <summary>
/// A per-camera or per-camera-group override of a global <see cref="Setting"/> key. Every setting
/// that is supposed to be "global with per-camera override" (retention, recording mode, motion
/// sensitivity, stream selection, storage target, ...) goes through this one mechanism rather than a
/// bespoke nullable override column per feature. See ISettingsResolver for the resolution order.
/// </summary>
public class SettingOverride
{
    public Guid Id { get; set; }
    public SettingScope Scope { get; set; }

    /// <summary>Camera.Id or CameraGroup.Id. Null when Scope is Global (Setting is used instead).</summary>
    public Guid? ScopeId { get; set; }

    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastModifiedAt { get; set; }
    public string? LastModifiedBy { get; set; }
}
