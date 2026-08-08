using Rcordr.Onvif.Capability;
using Rcordr.Onvif.Clients;

namespace Rcordr.Tests;

public class CameraProfileRankerTests
{
    // Reproduces the exact profile shapes returned by a real Amcrest IP5M-B1276EW-AI: Main and
    // Sub1 (H.265) report no encoder detail at all (a firmware gap, confirmed via both GetProfiles
    // and a direct GetVideoEncoderConfiguration call), while Sub2 (H.264) reports full 704x480
    // detail. A pure resolution-based ranking would rank Sub2 above Main.
    [Fact]
    public void Rank_PrefersNamedMainOverHigherResolutionUnnamedProfile()
    {
        var main = new OnvifMediaProfile("MediaProfile00000", "MediaProfile_Channel1_MainStream",
            null, null, null, null, null, "AAC", true, false);
        var sub1 = new OnvifMediaProfile("MediaProfile00001", "MediaProfile_Channel1_SubStream1",
            null, null, null, null, null, "AAC", true, false);
        var sub2 = new OnvifMediaProfile("MediaProfile00002", "MediaProfile_Channel1_SubStream2",
            "H264", 704, 480, 20, 512, "AAC", true, false);

        var ranked = CameraProfileRanker.Rank([main, sub1, sub2]);

        Assert.Equal(["MediaProfile00000", "MediaProfile00001", "MediaProfile00002"],
            ranked.Select(p => p.Token));
    }

    [Fact]
    public void Rank_FallsBackToResolutionWhenNamesGiveNoHint()
    {
        var high = new OnvifMediaProfile("high", "Profile1", "H264", 1920, 1080, 30, 4096, null, false, false);
        var low = new OnvifMediaProfile("low", "Profile2", "H264", 640, 480, 15, 512, null, false, false);

        var ranked = CameraProfileRanker.Rank([low, high]);

        Assert.Equal(["high", "low"], ranked.Select(p => p.Token));
    }
}
