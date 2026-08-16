using System.Text.Json;

namespace LarisVMS.Web.Helpers;

/// <summary>Server-side reads of View.LayoutJson — the shape itself
/// (<c>{"cells":[{"id","x","y","w","h","aspect","cameraId","hideOnPhone"}],"mobileTwoColumn":false}</c>)
/// is owned by the client-side editor (view-editor.js); this is only what the server genuinely needs
/// to read out of it without shipping GridStack: a cell count for Views/Index, and the camera set for
/// audit logging (which cameras a user actually watched).</summary>
public static class ViewLayout
{
    public static int CountCells(string? layoutJson) => ReadCells(layoutJson).Count;

    /// <summary>Distinct camera IDs referenced by this layout's cells, in layout order. Distinct
    /// because a view can legitimately place the same camera in more than one cell, and "watched
    /// camera X" is worth logging once per view opened, not once per tile it happens to occupy.
    /// Cells with a missing/unparseable cameraId are skipped rather than failing the whole read —
    /// this backs an audit entry, which must never be the thing that breaks opening a view.</summary>
    public static List<Guid> CameraIds(string? layoutJson)
    {
        var ids = new List<Guid>();
        foreach (var cell in ReadCells(layoutJson))
        {
            if (!cell.TryGetProperty("cameraId", out var cameraId)) continue;
            if (cameraId.ValueKind != JsonValueKind.String) continue;
            if (!Guid.TryParse(cameraId.GetString(), out var id)) continue;
            if (!ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>Cloned out of the JsonDocument rather than returned as live JsonElements — the
    /// document is disposed on the way out of this method, and a JsonElement doesn't outlive it.</summary>
    private static List<JsonElement> ReadCells(string? layoutJson)
    {
        var result = new List<JsonElement>();
        if (string.IsNullOrWhiteSpace(layoutJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(layoutJson);
            if (!doc.RootElement.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var cell in cells.EnumerateArray())
            {
                if (cell.ValueKind == JsonValueKind.Object) result.Add(cell.Clone());
            }
        }
        catch (JsonException)
        {
            // Corrupted layout — same "render/log what we can rather than fail" stance the client
            // editor takes when it can't parse its own layout.
        }
        return result;
    }
}
