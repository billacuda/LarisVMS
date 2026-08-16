namespace LarisVMS.Core;

/// <summary>
/// Builds the human-readable "what actually changed" string that every Update-style audit entry
/// passes as <c>AuditLog.Details</c> — e.g. <c>"Name: Front Door → Front Porch; Enabled: True → False"</c>.
///
/// Only fields whose value actually changed are listed, so a save that touched nothing reads as
/// nothing rather than as a wall of unchanged values. Secret fields (camera credentials, the node
/// registration key — anything the codebase already treats as sensitive, e.g. the
/// SecretProtection-encrypted columns on Camera) never have either value written out: they log only
/// that they changed. An audit trail that leaked the credential it was recording the change to would
/// be worse than no audit trail at all, since the log itself is readable by anyone with Logs.View.
/// </summary>
public static class AuditDiff
{
    /// <summary>One field's before/after. <paramref name="IsSecret"/> suppresses both values in the
    /// output — the field is still reported as changed, just never with its contents.</summary>
    public readonly record struct Field(string Key, string? OldValue, string? NewValue, bool IsSecret = false);

    /// <summary>Convenience for the common non-secret case, so call sites read as a flat list of
    /// key/old/new triples rather than repeating <c>false</c> on every line.</summary>
    public static Field Of(string key, string? oldValue, string? newValue) => new(key, oldValue, newValue);

    /// <summary>Same, for a value that must never be written to the log — see the class doc comment.</summary>
    public static Field Secret(string key, string? oldValue, string? newValue) => new(key, oldValue, newValue, true);

    /// <summary>For a secret whose old value genuinely isn't available to compare against — e.g. a
    /// camera's stored credentials, which CameraService.GetAsync deliberately never projects, so an
    /// edit form can only know "a new value was submitted", not whether it differs from the old one.
    /// Reports the field as changed when <paramref name="changed"/> is true and omits it otherwise;
    /// no value is ever written either way. Deliberately not done by passing sentinel strings to
    /// <see cref="Secret"/> — this states the intent at the call site instead of hiding it behind
    /// two placeholder values that only differ to trip the comparison.</summary>
    public static Field SecretChanged(string key, bool changed) =>
        new(key, changed ? "0" : "1", "1", true);

    /// <summary>Returns null when nothing changed, so a caller can choose to skip logging entirely
    /// for a no-op save rather than writing an entry with empty details.</summary>
    public static string? Build(params Field[] fields)
    {
        if (fields is null || fields.Length == 0) return null;

        var parts = new List<string>();
        foreach (var field in fields)
        {
            // Null and empty are treated as the same "not set" value: a nullable column cleared to
            // null and a text box submitted blank are the same edit from the user's point of view,
            // and reporting "Notes:  → " for one but not the other would be noise, not signal.
            var oldValue = field.OldValue ?? string.Empty;
            var newValue = field.NewValue ?? string.Empty;
            if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) continue;

            parts.Add(field.IsSecret
                ? $"{field.Key}: changed"
                : $"{field.Key}: {Display(oldValue)} → {Display(newValue)}");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>An empty value renders as "(none)" rather than as nothing at all — otherwise
    /// "Group:  → Parking" reads like a formatting bug instead of "this had no group before".</summary>
    private static string Display(string value) => value.Length == 0 ? "(none)" : value;
}
