using Rcordr.Core.Dtos;
using Rcordr.Core.Interfaces;
using Rcordr.Onvif.Discovery;

namespace Rcordr.Infrastructure.Services;

public class CameraDiscoveryService : ICameraDiscoveryService
{
    public async Task<IReadOnlyList<DiscoveredCameraDto>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var devices = await WsDiscoveryClient.ProbeAsync(timeout, ct);
        return devices
            .Select(d => new DiscoveredCameraDto(
                d.EndpointReference,
                d.XAddrs.Select(x => x.ToString()).ToList(),
                d.HardwareHint,
                d.NameHint,
                d.RemoteAddress))
            .OrderBy(d => d.RemoteAddress)
            .ToList();
    }
}
