using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Rcordr.Core.Dtos;
using Rcordr.Core.Entities;
using Rcordr.Core.Enums;
using Rcordr.Core.Interfaces;
using Rcordr.Infrastructure.Data;
using Rcordr.Onvif.Capability;
using Rcordr.Onvif.Clients;
using Rcordr.Onvif.Soap;

namespace Rcordr.Infrastructure.Services;

/// <summary>
/// Camera CRUD plus the ONVIF probing that populates CameraCapabilities and CameraStream. Reads
/// Camera.Username/Password as plaintext (the EncryptedNullableStringConverter on those properties
/// decrypts transparently on the way out of EF) and hands them to the Onvif client layer, which
/// never touches SecretProtection itself — encryption stays entirely in the Infrastructure/data
/// layer, per the plan's security section.
/// </summary>
public class CameraService(ApplicationDbContext db, Func<HttpClient> httpClientFactory) : ICameraService
{
    // Username/Password are deliberately left out of these two projections. SecretProtection.Unprotect
    // is fail-loud by design (a credential that silently degraded to unusable ciphertext would be worse
    // than an error), but EF materializes every selected column for a row in one pass — so a single
    // camera whose credentials were encrypted under a Data Protection key ring that no longer exists
    // (key rotation, a restored backup, or — as happened once during development — a value written by
    // a process using a different key ring) would throw while decrypting that row and take the entire
    // list down with it. Neither the Index list nor the Edit page's read side displays credentials
    // (Edit only shows them if the user retypes them), so there's no reason for either query to touch
    // that converter at all. ProbeAsync loads the full entity separately, since it genuinely needs the
    // decrypted credentials to connect to the camera, and is where a decrypt failure is actually
    // reported as an error rather than crashing the page.
    public async Task<List<Camera>> ListAsync(CancellationToken ct = default)
        => await db.Cameras
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(ProjectWithoutCredentials)
            .ToListAsync(ct);

    public async Task<Camera?> GetAsync(Guid id, CancellationToken ct = default)
        => await db.Cameras
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(ProjectWithoutCredentials)
            .FirstOrDefaultAsync(ct);

    // An Expression<Func<...>>, not a plain method — EF Core needs the actual expression tree (not a
    // compiled delegate) to translate the projection into a SQL column list plus the joins implied by
    // Group/Capabilities/Streams, which is what makes Select(ProjectWithoutCredentials) below work
    // without any .Include() calls and, critically, without ever selecting the Username/Password
    // columns into the query at all.
    private static readonly Expression<Func<Camera, Camera>> ProjectWithoutCredentials = c => new Camera
    {
        Id = c.Id,
        NodeId = c.NodeId,
        GroupId = c.GroupId,
        Name = c.Name,
        Host = c.Host,
        OnvifPort = c.OnvifPort,
        DeviceServiceUri = c.DeviceServiceUri,
        Manufacturer = c.Manufacturer,
        Model = c.Model,
        FirmwareVersion = c.FirmwareVersion,
        SerialNumber = c.SerialNumber,
        TimeZoneId = c.TimeZoneId,
        QuotaBytes = c.QuotaBytes,
        LensType = c.LensType,
        DewarpConfigJson = c.DewarpConfigJson,
        IsEnabled = c.IsEnabled,
        CreatedAt = c.CreatedAt,
        LastProbedAt = c.LastProbedAt,
        Group = c.Group,
        Node = c.Node,
        Capabilities = c.Capabilities,
        Streams = c.Streams
    };

    public async Task<Camera> AddAsync(AddCameraRequest request, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(request.DeviceServiceUri, UriKind.Absolute, out var deviceUri))
            throw new ArgumentException("Device service URI is not a valid absolute URI.", nameof(request));

        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Host = deviceUri.Host,
            // Uri.Port already resolves to the scheme's real default (80 for http, 443 for https)
            // when the URI has no explicit port — IsDefaultPort ? 80 : ... previously hardcoded 80
            // even for an https:// URI with no explicit port, mislabeling the stored port.
            OnvifPort = deviceUri.Port,
            DeviceServiceUri = request.DeviceServiceUri,
            Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username,
            Password = string.IsNullOrWhiteSpace(request.Password) ? null : request.Password,
            GroupId = request.GroupId,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };

        db.Cameras.Add(camera);
        await db.SaveChangesAsync(ct);

        await ProbeAsync(camera.Id, ct);

        return camera;
    }

    public async Task UpdateAsync(Guid id, string name, Guid? groupId, Guid? nodeId, string? username, string? password,
        bool isEnabled, long? quotaBytes, CancellationToken ct = default)
    {
        var camera = await db.Cameras.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new InvalidOperationException("Camera not found.");

        camera.Name = name;
        camera.GroupId = groupId;
        camera.NodeId = nodeId;
        camera.IsEnabled = isEnabled;
        camera.QuotaBytes = quotaBytes;
        // Blank fields leave the stored credential alone — the edit form never round-trips the
        // decrypted password back to the browser, so an empty submission must mean "unchanged",
        // not "clear it".
        if (!string.IsNullOrWhiteSpace(username)) camera.Username = username;
        if (!string.IsNullOrWhiteSpace(password)) camera.Password = password;

        await db.SaveChangesAsync(ct);
    }

    public async Task<Dictionary<Guid, long>> GetStorageUsageAsync(CancellationToken ct = default)
        => await db.Segments
            .GroupBy(s => s.CameraId)
            .Select(g => new { CameraId = g.Key, Bytes = g.Sum(s => s.SizeBytes) })
            .ToDictionaryAsync(x => x.CameraId, x => x.Bytes, ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // ExecuteDeleteAsync issues the DELETE directly rather than loading the entity first — a
        // camera whose credentials can't be decrypted (see the ListAsync/GetAsync comment above)
        // must still be deletable, since deleting a broken row is exactly what a user reaches for
        // when they see one. CameraCapabilities/CameraStream rows cascade at the database level
        // (DeleteBehavior.Cascade in OnModelCreating).
        await db.Cameras.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<CameraProbeSummary> ProbeAsync(Guid cameraId, CancellationToken ct = default)
    {
        Camera camera;
        try
        {
            camera = await db.Cameras.FirstOrDefaultAsync(c => c.Id == cameraId, ct)
                ?? throw new InvalidOperationException("Camera not found.");
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // The stored Username/Password couldn't be decrypted — most likely encrypted under a
            // Data Protection key ring that no longer exists (key rotation, a restored backup, or a
            // value written by a different process/key ring entirely). Record the attempt via a
            // column-only update (ExecuteUpdateAsync never touches the encrypted columns, so it
            // can't hit the same failure) and report it as a probe error instead of throwing out of
            // the request.
            await db.Cameras.Where(c => c.Id == cameraId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastProbedAt, DateTime.UtcNow), ct);
            return new CameraProbeSummary(false, false, false, false, false, false, false, false,
                false, false, false, false, 0,
                $"Stored credentials could not be decrypted ({ex.Message}). Delete and re-add this camera.");
        }

        camera.LastProbedAt = DateTime.UtcNow;

        if (!Uri.TryCreate(camera.DeviceServiceUri, UriKind.Absolute, out var deviceUri))
        {
            await db.SaveChangesAsync(ct);
            return new CameraProbeSummary(false, false, false, false, false, false, false, false,
                false, false, false, false, 0, "Camera has no valid device service URI.");
        }

        var credentials = camera.Username is null ? null : new OnvifCredentials(camera.Username, camera.Password ?? string.Empty);

        try
        {
            var soap = new OnvifSoapClient(httpClientFactory());
            var deviceClient = new OnvifDeviceClient(soap);
            var mediaClient = new OnvifMediaClient(soap);
            var prober = new CameraCapabilityProber(deviceClient, mediaClient);

            var result = await prober.ProbeAsync(deviceUri, credentials, ct);

            camera.Manufacturer = result.DeviceInfo.Manufacturer;
            camera.Model = result.DeviceInfo.Model;
            camera.FirmwareVersion = result.DeviceInfo.FirmwareVersion;
            camera.SerialNumber = result.DeviceInfo.SerialNumber;

            await UpsertCapabilitiesAsync(cameraId, result, ct);
            var streamCount = await ReplaceStreamsAsync(cameraId, result, mediaClient, credentials, ct);

            await db.SaveChangesAsync(ct);

            return new CameraProbeSummary(
                result.ProfileS, result.ProfileT, result.ProfileG, result.ProfileM,
                result.HasPtz, result.HasImaging, result.HasEvents, result.HasAnalyticsMetadata,
                result.HasMedia2, result.HasAudioOut, result.HasRelayOutputs, result.HasDigitalInputs,
                streamCount, Error: null);
        }
        catch (Exception ex) when (ex is OnvifFaultException or HttpRequestException or TaskCanceledException)
        {
            // Network/auth/protocol failure: keep whatever capabilities/streams were captured by a
            // previous successful probe rather than wiping them out because the device is
            // temporarily unreachable.
            await db.SaveChangesAsync(ct);
            return new CameraProbeSummary(false, false, false, false, false, false, false, false,
                false, false, false, false, 0, ex.Message);
        }
    }

    private async Task UpsertCapabilitiesAsync(Guid cameraId, CameraProbeResult result, CancellationToken ct)
    {
        var caps = await db.CameraCapabilities.FirstOrDefaultAsync(c => c.CameraId == cameraId, ct);
        if (caps is null)
        {
            caps = new CameraCapabilities { CameraId = cameraId };
            db.CameraCapabilities.Add(caps);
        }

        caps.ProfileS = result.ProfileS;
        caps.ProfileT = result.ProfileT;
        caps.ProfileG = result.ProfileG;
        caps.ProfileM = result.ProfileM;
        caps.HasPtz = result.HasPtz;
        caps.HasImaging = result.HasImaging;
        caps.HasEvents = result.HasEvents;
        caps.HasAnalyticsMetadata = result.HasAnalyticsMetadata;
        caps.HasMedia2 = result.HasMedia2;
        caps.HasAudioOut = result.HasAudioOut;
        caps.HasRelayOutputs = result.HasRelayOutputs;
        caps.HasDigitalInputs = result.HasDigitalInputs;
        caps.RawProbeJson = System.Text.Json.JsonSerializer.Serialize(result.RawXAddrs);
        caps.ProbedAt = DateTime.UtcNow;
    }

    /// <summary>Ranks profiles by resolution (highest first) and assigns Main/Sub/Third — ONVIF
    /// doesn't label which profile is "the" main stream, so pixel count is the practical proxy
    /// every other role assignment (recording, wall auto-switch) in later milestones builds on.</summary>
    private async Task<int> ReplaceStreamsAsync(Guid cameraId, CameraProbeResult result,
        OnvifMediaClient mediaClient, OnvifCredentials? credentials, CancellationToken ct)
    {
        var existing = await db.CameraStreams.Where(s => s.CameraId == cameraId).ToListAsync(ct);
        db.CameraStreams.RemoveRange(existing);

        if (result.Profiles.Count == 0) return 0;

        var mediaXAddrText = result.RawXAddrs.GetValueOrDefault("Media");
        if (mediaXAddrText is null || !Uri.TryCreate(mediaXAddrText, UriKind.Absolute, out var mediaXAddr))
            return 0;

        var ranked = CameraProfileRanker.Rank(result.Profiles);

        var roles = new[] { CameraStreamRole.Main, CameraStreamRole.Sub, CameraStreamRole.Third };
        var count = 0;

        for (var i = 0; i < ranked.Count; i++)
        {
            var profile = ranked[i];
            Uri? streamUri;
            try { streamUri = await mediaClient.GetStreamUriAsync(mediaXAddr, profile.Token, credentials, ct); }
            catch (OnvifFaultException) { continue; }
            if (streamUri is null) continue;

            db.CameraStreams.Add(new CameraStream
            {
                Id = Guid.NewGuid(),
                CameraId = cameraId,
                Role = roles[i],
                RtspUri = streamUri.ToString(),
                ProfileToken = profile.Token,
                Codec = profile.VideoEncoding,
                Width = profile.Width,
                Height = profile.Height,
                Fps = profile.FrameRateLimit,
                BitrateKbps = profile.BitrateLimitKbps,
                HasAudio = profile.HasAudio,
                AudioCodec = profile.AudioEncoding
            });
            count++;
        }

        return count;
    }
}
