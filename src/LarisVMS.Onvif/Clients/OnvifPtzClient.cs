using System.Globalization;
using LarisVMS.Onvif.Soap;

namespace LarisVMS.Onvif.Clients;

/// <summary>ONVIF ver20 PTZ service — continuous pan/tilt/zoom moves and stop. Only what "basic PTZ"
/// (M18) needs; presets (GetPresets/GotoPreset/SetPreset/RemovePreset) are a follow-up pass.</summary>
public class OnvifPtzClient(OnvifSoapClient soap)
{
    private const string PtzNs = "http://www.onvif.org/ver20/ptz/wsdl";
    private const string SchemaNs = "http://www.onvif.org/ver10/schema";

    /// <summary>Starts a continuous pan/tilt/zoom move at the given normalized velocity (each axis
    /// clamped to -1..1 by the caller; 0 means "don't move this axis"). The device auto-stops after
    /// timeoutSeconds unless a new ContinuousMove or an explicit Stop arrives first — this is a
    /// deliberate dead-man's-switch this app relies on: a lost network connection mid-press must not
    /// leave a camera panning forever, so the client re-issues this on a short interval for as long
    /// as a control is held (well inside timeoutSeconds) and always calls StopAsync on release,
    /// rather than trusting only one side of that safety net.</summary>
    public async Task ContinuousMoveAsync(Uri ptzServiceUri, string profileToken, double panX, double tiltY, double zoomX,
        int timeoutSeconds, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var body = $"""
            <ContinuousMove xmlns="{PtzNs}">
              <ProfileToken>{System.Security.SecurityElement.Escape(profileToken)}</ProfileToken>
              <Velocity>
                <PanTilt x="{FormatAxis(panX)}" y="{FormatAxis(tiltY)}" xmlns="{SchemaNs}"/>
                <Zoom x="{FormatAxis(zoomX)}" xmlns="{SchemaNs}"/>
              </Velocity>
              <Timeout>PT{timeoutSeconds}S</Timeout>
            </ContinuousMove>
            """;
        await soap.PostAsync(ptzServiceUri, $"{PtzNs}/ContinuousMove", body, credentials, ct);
    }

    public async Task StopAsync(Uri ptzServiceUri, string profileToken, OnvifCredentials? credentials, CancellationToken ct = default)
    {
        var body = $"""
            <Stop xmlns="{PtzNs}">
              <ProfileToken>{System.Security.SecurityElement.Escape(profileToken)}</ProfileToken>
              <PanTilt>true</PanTilt>
              <Zoom>true</Zoom>
            </Stop>
            """;
        await soap.PostAsync(ptzServiceUri, $"{PtzNs}/Stop", body, credentials, ct);
    }

    /// <summary>internal, not private: unit-tested directly against the exact strings a real ONVIF
    /// device's XML parser needs to see — invariant culture so a server running under e.g. a
    /// comma-decimal locale never emits "0,5" into an XML attribute.</summary>
    internal static string FormatAxis(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
