namespace LarisVMS.Core.Entities;

/// <summary>
/// A single per-user client preference (theme, last-watched view, table page size, playback clock
/// format, and so on) — the server-backed replacement for what used to live only in
/// <c>localStorage</c>, which meant every preference reset on a new browser or device and never
/// followed the user across a login. One row per (UserId, Key) pair; Value is always a string —
/// callers own their own serialization (a bool becomes "true"/"false", same convention
/// <see cref="Setting"/> already uses for admin-side config).
/// </summary>
public class UserPreference
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime LastModifiedAt { get; set; } = DateTime.UtcNow;
}
