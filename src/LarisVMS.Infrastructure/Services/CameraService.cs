using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Onvif.Capability;
using LarisVMS.Onvif.Clients;
using LarisVMS.Onvif.Soap;

namespace LarisVMS.Infrastructure.Services;

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
    // Groups/Capabilities/Streams, which is what makes Select(ProjectWithoutCredentials) below work
    // without any .Include() calls and, critically, without ever selecting the Username/Password
    // columns into the query at all.
    private static readonly Expression<Func<Camera, Camera>> ProjectWithoutCredentials = c => new Camera
    {
        Id = c.Id,
        NodeId = c.NodeId,
        Name = c.Name,
        Host = c.Host,
        OnvifPort = c.OnvifPort,
        DeviceServiceUri = c.DeviceServiceUri,
        Manufacturer = c.Manufacturer,
        Model = c.Model,
        FirmwareVersion = c.FirmwareVersion,
        SerialNumber = c.SerialNumber,
        IntegrationKey = c.IntegrationKey,
        VideoSourceToken = c.VideoSourceToken,
        TimeZoneId = c.TimeZoneId,
        QuotaBytes = c.QuotaBytes,
        LensType = c.LensType,
        DewarpConfigJson = c.DewarpConfigJson,
        AiDetectionEnabled = c.AiDetectionEnabled,
        MotionDetectionSource = c.MotionDetectionSource,
        IsEnabled = c.IsEnabled,
        CreatedAt = c.CreatedAt,
        LastProbedAt = c.LastProbedAt,
        Groups = c.Groups,
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
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };

        // One initial group at creation time, same UX as before this camera could belong to several —
        // more groups are added afterward via SetCameraGroupsAsync (Cameras/Edit's own picker, or
        // Groups.cshtml's per-group camera picker), not here.
        if (request.GroupId is { } groupId)
        {
            var group = await db.CameraGroups.FindAsync([groupId], ct);
            if (group is not null) camera.Groups.Add(group);
        }

        // Every camera belongs to the built-in "All Cameras" group unconditionally — see its own doc
        // comment. Guarded against a double-add on the off chance request.GroupId was already it.
        var allCamerasGroup = await db.CameraGroups.FindAsync([CameraGroup.AllCamerasId], ct);
        if (allCamerasGroup is not null && camera.Groups.All(g => g.Id != allCamerasGroup.Id))
            camera.Groups.Add(allCamerasGroup);

        db.Cameras.Add(camera);
        await db.SaveChangesAsync(ct);

        await ProbeAsync(camera.Id, ct);

        return camera;
    }

    public async Task UpdateAsync(Guid id, string name, Guid? nodeId, string? username, string? password,
        bool isEnabled, long? quotaBytes, string? deviceServiceUri = null,
        bool aiDetectionEnabled = false, MotionDetectionSource? motionDetectionSource = null,
        CancellationToken ct = default)
    {
        var camera = await db.Cameras.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new InvalidOperationException("Camera not found.");

        camera.Name = name;
        camera.NodeId = nodeId;
        camera.IsEnabled = isEnabled;
        camera.QuotaBytes = quotaBytes;
        camera.AiDetectionEnabled = aiDetectionEnabled;
        camera.MotionDetectionSource = motionDetectionSource;
        // Blank fields leave the stored credential alone — the edit form never round-trips the
        // decrypted password back to the browser, so an empty submission must mean "unchanged",
        // not "clear it".
        if (!string.IsNullOrWhiteSpace(username)) camera.Username = username;
        if (!string.IsNullOrWhiteSpace(password)) camera.Password = password;

        // Blank also means "unchanged" here, same convention as credentials above — the form always
        // submits the current value, so blank only happens if a caller deliberately omits it.
        // Changing the scheme (http <-> https) is the main reason this exists, but any change to
        // host/port/path is accepted the same way: whatever a re-probe or WS-Discovery would have
        // written on initial add is exactly what's recomputed here, so this can't drift from AddAsync.
        if (!string.IsNullOrWhiteSpace(deviceServiceUri) && deviceServiceUri != camera.DeviceServiceUri)
        {
            if (!Uri.TryCreate(deviceServiceUri, UriKind.Absolute, out var deviceUri))
                throw new ArgumentException("Device service URI is not a valid absolute URI.", nameof(deviceServiceUri));

            camera.DeviceServiceUri = deviceServiceUri;
            camera.Host = deviceUri.Host;
            camera.OnvifPort = deviceUri.Port;
            // A changed address (especially a changed scheme) can mean the device's own capability
            // report is now stale — a probe that never ran against https, for instance. Not fatal if
            // it fails: the address change itself is still saved, and the existing "Probe failed" UI
            // on Cameras/Edit already tells the operator what's wrong. IntegrationBaseUri needs no
            // separate update — NodeService.ResolveIntegrationBaseUri derives it from
            // DeviceServiceUri fresh on every config generation, never stored.
            await db.SaveChangesAsync(ct);
            try { await ProbeAsync(id, ct); } catch { /* surfaced to the operator via LastError, not thrown here */ }
            return;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task SetCameraGroupsAsync(Guid cameraId, IReadOnlyList<Guid> groupIds, CancellationToken ct = default)
    {
        var camera = await db.Cameras.Include(c => c.Groups).FirstOrDefaultAsync(c => c.Id == cameraId, ct)
            ?? throw new InvalidOperationException("Camera not found.");

        // The built-in "All Cameras" group is exempt from the single-site rule below (it's top-level
        // and on every camera, so naively including it would collide with each camera's real site) and
        // always re-added regardless of what the caller passed — this is the one method every group-
        // membership mutation funnels through, which is what makes "can't be removed" true everywhere
        // without needing to special-case every caller individually.
        var distinctIds = groupIds.Distinct().Where(id => id != CameraGroup.AllCamerasId).ToList();
        if (distinctIds.Count > 0)
        {
            // Small table (sites/buildings/floors), loaded whole rather than filtered — CameraGroupPolicy
            // needs every ancestor's ParentId to walk up to each candidate group's Site, not just the
            // candidates themselves.
            var parentIdByGroupId = await db.CameraGroups.AsNoTracking()
                .Select(g => new { g.Id, g.ParentId }).ToDictionaryAsync(g => g.Id, g => g.ParentId, ct);
            if (!CameraGroupPolicy.AllShareOneSite(distinctIds, parentIdByGroupId))
                throw new InvalidOperationException("A camera can only belong to groups under a single site.");
        }

        var targetGroups = distinctIds.Count == 0
            ? []
            : await db.CameraGroups.Where(g => distinctIds.Contains(g.Id)).ToListAsync(ct);

        var allCamerasGroup = await db.CameraGroups.FindAsync([CameraGroup.AllCamerasId], ct);
        if (allCamerasGroup is not null) targetGroups.Add(allCamerasGroup);

        camera.Groups.Clear();
        foreach (var group in targetGroups) camera.Groups.Add(group);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Dictionary<Guid, long>> GetStorageUsageAsync(CancellationToken ct = default)
        => await db.Segments
            .GroupBy(s => s.CameraId)
            .Select(g => new { CameraId = g.Key, Bytes = g.Sum(s => s.SizeBytes) })
            .ToDictionaryAsync(x => x.CameraId, x => x.Bytes, ct);

    public async Task UpdateStreamAsync(Guid streamId, bool isEnabled, string? customName, CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(customName) ? null : customName.Trim();
        await db.CameraStreams.Where(s => s.Id == streamId).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.IsEnabled, isEnabled)
            .SetProperty(s => s.CustomName, name), ct);
    }

    public async Task ToggleEnabledAsync(Guid id, CancellationToken ct = default)
        => await db.Cameras.Where(c => c.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.IsEnabled, c => !c.IsEnabled), ct);

    public async Task<Dictionary<Guid, List<Guid>>> GetStaleSegmentNodeIdsAsync(CancellationToken ct = default)
    {
        var staleRows = await (
            from s in db.Segments
            join c in db.Cameras on s.CameraId equals c.Id
            where s.NodeId != c.NodeId
            select new { s.CameraId, s.NodeId })
            .Distinct()
            .ToListAsync(ct);

        return staleRows
            .GroupBy(r => r.CameraId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.NodeId).ToList());
    }

    public async Task<List<StaleSegmentDetail>> GetStaleSegmentDetailsAsync(CancellationToken ct = default)
        => await (
            from s in db.Segments
            join c in db.Cameras on s.CameraId equals c.Id
            where s.NodeId != c.NodeId
            group s by new { s.CameraId, c.Name, s.NodeId } into g
            select new StaleSegmentDetail(g.Key.CameraId, g.Key.Name, g.Key.NodeId, g.Max(x => x.EndUtc)))
            .ToListAsync(ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // ExecuteDeleteAsync issues the DELETE directly rather than loading the entity first — a
        // camera whose credentials can't be decrypted (see the ListAsync/GetAsync comment above)
        // must still be deletable, since deleting a broken row is exactly what a user reaches for
        // when they see one. CameraCapabilities/CameraStream rows cascade at the database level
        // (DeleteBehavior.Cascade in OnModelCreating).
        await db.Cameras.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<int> SplitChannelsAsync(Guid cameraId, CancellationToken ct = default)
    {
        // .Include(c => c.Groups): each split-off sibling below copies this camera's group
        // membership, which needs the collection actually loaded rather than the empty in-memory
        // default a plain FirstOrDefaultAsync would leave it as.
        var camera = await db.Cameras.Include(c => c.Groups).FirstOrDefaultAsync(c => c.Id == cameraId, ct)
            ?? throw new InvalidOperationException("Camera not found.");

        var summary = await ProbeAsync(cameraId, ct);
        var channels = summary.VideoSourceTokens ?? [];
        if (channels.Count < 2) return 0;

        // Every camera already covering one of this device's sensors — including this one. Keyed by
        // token so a re-split (or a split run after some channels were added and others deleted) adds
        // only what's genuinely missing instead of duplicating rows, which is the same identity-
        // matching principle ReplaceStreamsAsync already applies to profiles within one camera.
        var siblings = await db.Cameras
            .Where(c => c.DeviceServiceUri == camera.DeviceServiceUri && c.VideoSourceToken != null)
            .ToListAsync(ct);
        var taken = siblings.Select(c => c.VideoSourceToken!).ToHashSet(StringComparer.Ordinal);

        // The camera being split claims the first channel rather than staying unpinned: leaving it
        // null would mean it keeps ranking across *every* lens's profiles (see ReplaceStreamsAsync),
        // so it could hand itself the same stream a sibling is already recording.
        if (camera.VideoSourceToken is null)
        {
            camera.VideoSourceToken = channels[0];
            taken.Add(channels[0]);
            camera.Name = ChannelName(camera.Name, 1);
        }

        var createdIds = new List<Guid>();
        for (var i = 0; i < channels.Count; i++)
        {
            if (taken.Contains(channels[i])) continue;

            // Shares Host/DeviceServiceUri/credentials with its siblings — one physical device, but
            // as far as recording/Views/Live/Playback are concerned these are N ordinary independent
            // cameras, which is exactly why none of those subsystems needed changes for this.
            var channelCamera = new Camera
            {
                Id = Guid.NewGuid(),
                Name = ChannelName(BaseName(camera.Name), i + 1),
                Host = camera.Host,
                OnvifPort = camera.OnvifPort,
                DeviceServiceUri = camera.DeviceServiceUri,
                Username = camera.Username,
                Password = camera.Password,
                Groups = camera.Groups.ToList(),
                NodeId = camera.NodeId,
                VideoSourceToken = channels[i],
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow
            };
            db.Cameras.Add(channelCamera);
            createdIds.Add(channelCamera.Id);
            taken.Add(channels[i]);
        }

        await db.SaveChangesAsync(ct);

        // Probed after the save, so every row exists (and already carries its VideoSourceToken)
        // before any probe runs against it and resolves that lens's own streams.
        foreach (var createdId in createdIds) await ProbeAsync(createdId, ct);

        // The split camera itself is re-probed last: the probe at the top of this method ran while it
        // was still unpinned, so its streams were ranked across every lens rather than just its own.
        await ProbeAsync(cameraId, ct);

        return createdIds.Count;
    }

    /// <summary>"Front Door" -> "Front Door — Ch1". Idempotent against an already-suffixed name so a
    /// re-split doesn't produce "Front Door — Ch1 — Ch1".</summary>
    private static string ChannelName(string name, int channelNumber) => $"{BaseName(name)} — Ch{channelNumber}";

    private static string BaseName(string name)
    {
        var marker = name.LastIndexOf(" — Ch", StringComparison.Ordinal);
        return marker < 0 ? name : name[..marker];
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

            // Re-evaluated on every probe rather than only at first add, so a camera picks up (or
            // loses) an integration automatically when its reported identity changes — a firmware
            // update that starts reporting a different model, or a provider added in a later release
            // that now recognizes hardware already in the system.
            camera.IntegrationKey = CameraIntegrations.Detect(camera.Manufacturer, camera.Model)?.Key;

            await UpsertCapabilitiesAsync(cameraId, result, ct);
            var streamCount = await ReplaceStreamsAsync(cameraId, result, mediaClient, credentials,
                camera.VideoSourceToken, ct);

            await db.SaveChangesAsync(ct);

            // Every distinct physical sensor this device exposes, in the order GetProfiles listed
            // them. One entry (or none, for firmware that omits VideoSourceConfiguration) is the
            // ordinary single-sensor camera; more than one means the device has multiple lenses that
            // can each be split into their own Camera row — see SplitChannelsAsync.
            var channels = result.Profiles
                .Select(p => p.VideoSourceToken)
                .Where(t => t is not null)
                .Distinct()
                .ToList();

            return new CameraProbeSummary(
                result.ProfileS, result.ProfileT, result.ProfileG, result.ProfileM,
                result.HasPtz, result.HasImaging, result.HasEvents, result.HasAnalyticsMetadata,
                result.HasMedia2, result.HasAudioOut, result.HasRelayOutputs, result.HasDigitalInputs,
                streamCount, Error: null, VideoSourceTokens: channels!);
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
    /// every other role assignment (recording, wall auto-switch) in later milestones builds on.
    ///
    /// Not a blind delete+recreate: IsEnabled and CustomName are user overrides that must survive a
    /// re-probe, so existing rows are matched to the freshly-probed profiles by ProfileToken first
    /// (the stable ONVIF identifier), falling back to Role for a camera whose firmware reassigns
    /// tokens on every probe. Matched rows keep their Id/IsEnabled/CustomName and have everything
    /// else refreshed in place; unmatched old rows (a profile that's gone) are removed; unmatched new
    /// profiles are inserted fresh with IsEnabled defaulting true.</summary>
    private async Task<int> ReplaceStreamsAsync(Guid cameraId, CameraProbeResult result,
        OnvifMediaClient mediaClient, OnvifCredentials? credentials, string? videoSourceToken,
        CancellationToken ct)
    {
        var existing = await db.CameraStreams.Where(s => s.CameraId == cameraId).ToListAsync(ct);

        // A camera pinned to one sensor of a multi-sensor device only ever considers that sensor's
        // own profiles — otherwise CameraProfileRanker, which ranks purely on name/resolution and
        // knows nothing about channels, would happily hand this camera another lens's stream as its
        // "Main". Null (the ordinary single-sensor camera, and everything added before multi-channel
        // support) keeps the original behavior of ranking across every profile the device reports.
        var candidateProfiles = videoSourceToken is null
            ? result.Profiles
            : result.Profiles.Where(p => p.VideoSourceToken == videoSourceToken).ToList();

        if (candidateProfiles.Count == 0)
        {
            db.CameraStreams.RemoveRange(existing);
            return 0;
        }

        var mediaXAddrText = result.RawXAddrs.GetValueOrDefault("Media");
        if (mediaXAddrText is null || !Uri.TryCreate(mediaXAddrText, UriKind.Absolute, out var mediaXAddr))
        {
            db.CameraStreams.RemoveRange(existing);
            return 0;
        }

        var ranked = CameraProfileRanker.Rank(candidateProfiles);
        var roles = new[] { CameraStreamRole.Main, CameraStreamRole.Sub, CameraStreamRole.Third };
        var byToken = existing.ToDictionary(s => s.ProfileToken);
        var byRole = existing.ToDictionary(s => s.Role);
        var matched = new HashSet<Guid>();
        var count = 0;

        for (var i = 0; i < ranked.Count; i++)
        {
            var profile = ranked[i];
            var role = roles[i];
            Uri? streamUri;
            try { streamUri = await mediaClient.GetStreamUriAsync(mediaXAddr, profile.Token, credentials, ct); }
            catch (OnvifFaultException) { continue; }
            if (streamUri is null) continue;

            var current = byToken.GetValueOrDefault(profile.Token) ?? byRole.GetValueOrDefault(role);
            if (current is null)
            {
                current = new CameraStream { Id = Guid.NewGuid(), CameraId = cameraId };
                db.CameraStreams.Add(current);
            }
            else
            {
                matched.Add(current.Id);
            }

            current.Role = role;
            current.RtspUri = streamUri.ToString();
            current.ProfileToken = profile.Token;
            // ?? current.*, not a flat overwrite: ONVIF omits VideoEncoderConfiguration entirely for
            // some cameras/profiles (confirmed: Amcrest, H.265), so a re-probe reporting null here
            // must not clobber a real value RecordingSession already measured from ffmpeg's own
            // stderr and reported back via UpdateStreamInfoAsync — a fresher ONVIF-reported value
            // still wins when the camera actually provides one.
            current.Codec = profile.VideoEncoding ?? current.Codec;
            current.Width = profile.Width ?? current.Width;
            current.Height = profile.Height ?? current.Height;
            current.Fps = profile.FrameRateLimit;
            current.BitrateKbps = profile.BitrateLimitKbps;
            current.HasAudio = profile.HasAudio;
            current.AudioCodec = profile.AudioEncoding;
            count++;
        }

        db.CameraStreams.RemoveRange(existing.Where(s => !matched.Contains(s.Id)));

        return count;
    }
}
