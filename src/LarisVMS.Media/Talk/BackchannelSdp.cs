namespace LarisVMS.Media.Talk;

/// <summary>The camera's audio backchannel as its SDP describes it: where to SETUP it, and which G.711
/// flavour to send.</summary>
public sealed record BackchannelTrack(string ControlUri, int PayloadType, bool ALaw, int ClockRate);

/// <summary>Finds the ONVIF backchannel in a DESCRIBE answer. Per the ONVIF Streaming spec, a camera
/// answering a DESCRIBE that carries <c>Require: www.onvif.org/ver20/backchannel</c> adds an audio
/// media section marked <c>a=sendonly</c> (the client sends on it) alongside its normal recvonly
/// tracks. Only PCMU and PCMA at 8 kHz are used; µ-law is preferred when both are offered.</summary>
public static class BackchannelSdp
{
    public static BackchannelTrack? Parse(string sdp, Uri baseUri)
    {
        var lines = sdp.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        List<string>? section = null;
        var sections = new List<List<string>>();
        foreach (var line in lines)
        {
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                section = [line];
                sections.Add(section);
            }
            else section?.Add(line);
        }

        foreach (var media in sections)
        {
            var m = media[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (m.Length < 4 || m[0] != "m=audio") continue;
            if (!media.Any(l => l == "a=sendonly")) continue;

            var rtpmap = new Dictionary<int, string>();
            foreach (var a in media.Where(l => l.StartsWith("a=rtpmap:", StringComparison.Ordinal)))
            {
                var parts = a["a=rtpmap:".Length..].Split(' ', 2);
                if (parts.Length == 2 && int.TryParse(parts[0], out var pt)) rtpmap[pt] = parts[1].Trim();
            }

            var offered = m.Skip(3).Select(p => int.TryParse(p, out var pt) ? pt : -1).Where(pt => pt >= 0).ToList();
            (int Pt, bool ALaw)? Choose(string encoding, int staticPt, bool aLaw)
            {
                foreach (var pt in offered)
                {
                    var map = rtpmap.GetValueOrDefault(pt);
                    if (map is not null ? map.StartsWith(encoding + "/8000", StringComparison.OrdinalIgnoreCase) : pt == staticPt)
                        return (pt, aLaw);
                }
                return null;
            }
            var choice = Choose("PCMU", 0, false) ?? Choose("PCMA", 8, true);
            if (choice is null) continue;

            var control = media.FirstOrDefault(l => l.StartsWith("a=control:", StringComparison.Ordinal))?["a=control:".Length..].Trim();
            return new BackchannelTrack(ResolveControl(control, baseUri), choice.Value.Pt, choice.Value.ALaw, 8000);
        }
        return null;
    }

    /// <summary>A control attribute is absolute, "*" (the base itself), or relative to the base.</summary>
    internal static string ResolveControl(string? control, Uri baseUri)
    {
        if (string.IsNullOrEmpty(control) || control == "*") return baseUri.ToString();
        if (control.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) || control.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
            return control;
        return baseUri.ToString().TrimEnd('/') + "/" + control.TrimStart('/');
    }
}
