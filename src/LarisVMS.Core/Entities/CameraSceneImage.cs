namespace LarisVMS.Core.Entities;

/// <summary>
/// A picture someone uploaded to show under a camera's scene heatmap in place of the automatic frame
/// from its recording — a clear daytime shot of the same view, say. One per camera; its own table so
/// camera queries never load image bytes. Deleted with the camera.
/// </summary>
public class CameraSceneImage
{
    public Guid CameraId { get; set; }
    public byte[] Image { get; set; } = [];

    /// <summary>Detected from the file's own bytes (PNG, JPEG, GIF or WebP), never the browser's claim.</summary>
    public string ContentType { get; set; } = "";

    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime UploadedUtc { get; set; }
    public string? UploadedBy { get; set; }
}
