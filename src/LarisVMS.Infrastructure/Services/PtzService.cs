using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Onvif.Clients;
using LarisVMS.Onvif.Soap;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// M18 "basic PTZ". Resolves a camera's PTZ target (service XAddr from
/// CameraCapabilities.RawProbeJson — same "raw category XAddr map" NodeService.ResolveEventsServiceUri
/// already reads for the Events service; Main stream's ONVIF profile token; decrypted credentials)
/// and calls OnvifPtzClient directly, same "onvif" named HttpClient CameraService's own probing
/// already uses — no node hop, since this is a quick request/response, not a long-lived session.
/// </summary>
public class PtzService(ApplicationDbContext db, Func<HttpClient> httpClientFactory) : IPtzService
{
    // Comfortably inside OnvifPtzClient.ContinuousMoveAsync's own device-side auto-stop window —
    // this app's client re-issues MoveAsync well before the device would time the move out on its
    // own, and always calls StopAsync on release, so this value is a safety margin, not the thing
    // actually keeping a held control moving in practice.
    private const int MoveTimeoutSeconds = 5;

    public async Task<bool> MoveAsync(Guid cameraId, double panX, double tiltY, double zoomX, CancellationToken ct = default)
    {
        var target = await ResolveAsync(cameraId, ct);
        if (target is not { } t) return false;

        var client = new OnvifPtzClient(new OnvifSoapClient(httpClientFactory()));
        await client.ContinuousMoveAsync(t.PtzXAddr, t.ProfileToken,
            Math.Clamp(panX, -1, 1), Math.Clamp(tiltY, -1, 1), Math.Clamp(zoomX, -1, 1),
            MoveTimeoutSeconds, t.Credentials, ct);
        return true;
    }

    public async Task<bool> StopAsync(Guid cameraId, CancellationToken ct = default)
    {
        var target = await ResolveAsync(cameraId, ct);
        if (target is not { } t) return false;

        var client = new OnvifPtzClient(new OnvifSoapClient(httpClientFactory()));
        await client.StopAsync(t.PtzXAddr, t.ProfileToken, t.Credentials, ct);
        return true;
    }

    private readonly record struct PtzTarget(Uri PtzXAddr, string ProfileToken, OnvifCredentials? Credentials);

    private async Task<PtzTarget?> ResolveAsync(Guid cameraId, CancellationToken ct)
    {
        // Username/Password decrypt transparently via EF's EncryptedNullableStringConverter, same as
        // any other read of these columns — projected here rather than loading the whole Camera
        // entity since PTZ is issued frequently (a held directional button re-fires this) and has no
        // use for anything else on it.
        var row = await db.Cameras
            .Where(c => c.Id == cameraId)
            .Select(c => new
            {
                c.Username,
                c.Password,
                HasPtz = c.Capabilities != null && c.Capabilities.HasPtz,
                RawProbeJson = c.Capabilities != null ? c.Capabilities.RawProbeJson : null,
                MainProfileToken = c.Streams.Where(s => s.Role == CameraStreamRole.Main).Select(s => s.ProfileToken).FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);

        if (row is null || !row.HasPtz || row.RawProbeJson is null || string.IsNullOrEmpty(row.MainProfileToken)) return null;

        Uri? ptzXAddr;
        try
        {
            var rawXAddrs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(row.RawProbeJson);
            var xAddrText = rawXAddrs?.GetValueOrDefault("PTZ");
            if (xAddrText is null || !Uri.TryCreate(xAddrText, UriKind.Absolute, out ptzXAddr)) return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // corrupted or pre-PTZ RawProbeJson shape — treat as unresolvable, not fatal
        }

        var credentials = row.Username is null ? null : new OnvifCredentials(row.Username, row.Password ?? string.Empty);
        return new PtzTarget(ptzXAddr, row.MainProfileToken, credentials);
    }
}
