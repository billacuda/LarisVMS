using System.Text.Json;

namespace NidusVMS.Web.Helpers;

/// <summary>Server-side reads of View.LayoutJson — the shape itself
/// (<c>{"cells":[...],"mobileTwoColumn":false}</c>) is owned by the client-side editor
/// (view-editor.js); this is only what Views/Index needs to show a cell count without shipping
/// GridStack there.</summary>
public static class ViewLayout
{
    public static int CountCells(string? layoutJson)
    {
        if (string.IsNullOrWhiteSpace(layoutJson)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(layoutJson);
            return doc.RootElement.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.Array
                ? cells.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
