using System.Globalization;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Security;

namespace LarisVMS.Web.Services;

/// <summary>
/// The request for one AI-detection span's cropped best-frame picture from its recording node
/// (/snapshot-image/{cameraId} on the node). Shared by the browser-facing proxy route in Program.cs and
/// the Home Assistant bridge, which publishes the picture over MQTT.
/// </summary>
public static class SnapshotImageRequest
{
    /// <summary>Whether the node for this picture has reported what's needed to ask it.</summary>
    public static bool CanRequest(SnapshotImageInfo info) =>
        info.NodeIp is not null && info.NodeLivePort is not null && info.NodeMediaSigningKey is not null;

    /// <summary>Reuses the hover-thumbnail token (cameraId + filePath + offsetSeconds). The box and
    /// frame size ride unsigned: they only change which part of an already-authorized frame is cut.</summary>
    public static HttpRequestMessage Create(Guid cameraId, SnapshotImageInfo info, int offsetSeconds, long offsetMs, long bestFrameTicks)
    {
        var token = MediaToken.IssueForThumbnail(cameraId, info.FilePath, offsetSeconds, info.NodeMediaSigningKey!, TimeSpan.FromSeconds(30));
        var nodeUri = $"http://{info.NodeIp}:{info.NodeLivePort}/snapshot-image/{cameraId}" +
            $"?path={Uri.EscapeDataString(info.FilePath)}&offset={offsetSeconds}&offsetMs={offsetMs}&token={Uri.EscapeDataString(token)}" +
            $"&spanId={info.SpanId}&bestFrameTicks={bestFrameTicks}" +
            $"&x={info.BoxX.ToString(CultureInfo.InvariantCulture)}&y={info.BoxY.ToString(CultureInfo.InvariantCulture)}" +
            $"&w={info.BoxW.ToString(CultureInfo.InvariantCulture)}&h={info.BoxH.ToString(CultureInfo.InvariantCulture)}" +
            $"&frameW={info.FrameWidth}&frameH={info.FrameHeight}";
        // See MediaTokenRequest's own doc comment: also sent as ?token= above for a node that hasn't
        // updated yet.
        return MediaTokenRequest.Create(HttpMethod.Get, nodeUri, token);
    }
}
