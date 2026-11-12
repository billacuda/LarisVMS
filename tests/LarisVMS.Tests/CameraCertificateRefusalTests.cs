using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>A node keeps the camera certificates it refused for its next heartbeat, once per address
/// an hour, and keeps them if the heartbeat doesn't get through.</summary>
public class CameraCertificateRefusalTests
{
    [Fact]
    public void RefusalsWaitForTheNextHeartbeatOncePerAddress()
    {
        var host = "cam-" + Guid.NewGuid().ToString("N") + ".test";
        CameraCertificateRefusals.Drain();

        CameraCertificateRefusals.Add(host, "AA", new string('B', 64));
        CameraCertificateRefusals.Add(host, "AA", new string('C', 64)); // same address within the hour

        var sent = CameraCertificateRefusals.Drain();
        var refusal = Assert.Single(sent!, r => r.Host == host);
        Assert.Equal(new string('B', 64), refusal.Presented);

        CameraCertificateRefusals.Requeue(sent);                        // the heartbeat failed
        Assert.Contains(CameraCertificateRefusals.Drain()!, r => r.Host == host);
        Assert.Null(CameraCertificateRefusals.Drain());
    }
}
