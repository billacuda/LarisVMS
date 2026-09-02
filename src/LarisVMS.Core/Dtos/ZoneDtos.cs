namespace LarisVMS.Core.Dtos;

/// <summary>Body for both create and update — an update simply supplies the Id-bearing route
/// segment separately, same shape either way.</summary>
public record SaveZoneRequest(string Name, string Kind, string PolygonJson, double Sensitivity, bool IsEnabled = true);

/// <summary>Response shape for the zones list/create/update endpoints — Kind as a string, same
/// convention SaveZoneRequest's own Kind already uses, and for the same reason: the raw Zone entity
/// has no [JsonConverter] on ZoneKind and this app registers no global JsonStringEnumConverter, so
/// returning the entity directly (as these endpoints did before this DTO existed) silently serializes
/// Kind as a bare integer — zones-editor.js's KIND_COLORS[z.kind]/KIND_LABELS[z.kind] lookups and its
/// zone-edit-form's Kind &lt;select&gt; value assignment are all keyed by the string name, so every
/// zone silently rendered/edited as if it were ServerMotion regardless of its real kind, confirmed
/// live (an Ignore zone never actually showed red, and re-opening any non-ServerMotion zone's edit
/// form never selected the right option).</summary>
public record ZoneDto(Guid Id, Guid CameraId, string Name, string Kind, string PolygonJson, double Sensitivity, bool IsEnabled);
