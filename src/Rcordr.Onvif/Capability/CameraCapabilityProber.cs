using Rcordr.Onvif.Clients;
using Rcordr.Onvif.Soap;

namespace Rcordr.Onvif.Capability;

/// <summary>
/// Orchestrates GetDeviceInformation/GetCapabilities/GetServices/GetProfiles into the ONVIF
/// Profile S/T/G/M matrix. This is inference from which services a device advertises, not
/// certified ONVIF conformance testing — see CameraCapabilities' doc comment. The heuristics:
///
///   S (streaming)  — the ver10 Media service answered GetProfiles with at least one profile.
///   T (advanced)   — the device advertises a ver20 media2 service (real Profile T devices
///                    implement media2; this is the closest cheap proxy without a conformance suite).
///   G (recording)  — GetServices lists a recording/search/replay namespace.
///   M (metadata)   — GetServices lists an analytics namespace *and* the device has an events
///                    service (metadata streaming rides the events/analytics pairing).
/// </summary>
public class CameraCapabilityProber(OnvifDeviceClient deviceClient, OnvifMediaClient mediaClient)
{
    public async Task<CameraProbeResult> ProbeAsync(
        Uri deviceServiceUri, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var deviceInfo = await deviceClient.GetDeviceInformationAsync(deviceServiceUri, credentials, ct);
        var capabilities = await deviceClient.GetCapabilitiesAsync(deviceServiceUri, credentials, ct);

        IReadOnlyList<OnvifServiceEntry> services;
        try { services = await deviceClient.GetServicesAsync(deviceServiceUri, credentials, ct); }
        catch (OnvifFaultException) { services = []; } // older Profile-S-only devices may not implement GetServices

        var hasMedia2 = services.Any(s => s.Namespace.Contains("ver20/media/wsdl", StringComparison.OrdinalIgnoreCase));
        var hasRecording = services.Any(s =>
            s.Namespace.Contains("/recording/wsdl", StringComparison.OrdinalIgnoreCase) ||
            s.Namespace.Contains("/search/wsdl", StringComparison.OrdinalIgnoreCase) ||
            s.Namespace.Contains("/replay/wsdl", StringComparison.OrdinalIgnoreCase));
        var hasAnalyticsService = services.Any(s => s.Namespace.Contains("/analytics/wsdl", StringComparison.OrdinalIgnoreCase))
            || capabilities.AnalyticsXAddr is not null;

        IReadOnlyList<OnvifMediaProfile> profiles = [];
        if (capabilities.MediaXAddr is not null)
        {
            try { profiles = await mediaClient.GetProfilesAsync(capabilities.MediaXAddr, credentials, ct); }
            catch (OnvifFaultException) { /* Media XAddr advertised but not actually reachable/authorized */ }
        }

        var profileS = profiles.Count > 0;
        var profileT = hasMedia2;
        var profileG = hasRecording;
        var hasEvents = capabilities.EventsXAddr is not null;
        var profileM = hasAnalyticsService && hasEvents;
        var hasAudioOut = profiles.Any(p => p.HasAudioOutputConfig);

        return new CameraProbeResult(
            deviceInfo, profileS, profileT, profileG, profileM,
            HasPtz: capabilities.PtzXAddr is not null,
            HasImaging: capabilities.ImagingXAddr is not null,
            HasEvents: hasEvents,
            HasAnalyticsMetadata: hasAnalyticsService,
            HasMedia2: hasMedia2,
            HasAudioOut: hasAudioOut,
            // Best-effort proxy: presence of the DeviceIO service, not a query of the actual relay/
            // input token lists (GetRelayOutputs/GetDigitalInputs) — refined if/when M9 needs the
            // real token list to drive relays.
            HasRelayOutputs: capabilities.DeviceIOXAddr is not null,
            HasDigitalInputs: capabilities.DeviceIOXAddr is not null,
            Profiles: profiles,
            RawXAddrs: capabilities.RawCategoryXAddrs);
    }
}
