using LarisVMS.Onvif.Clients;

namespace LarisVMS.Onvif.Capability;

/// <summary>
/// Ranks ONVIF media profiles into Main/Sub/Third order. ONVIF does not label which profile is
/// "the" main stream, so this is inference — and pixel count alone is not a safe signal: confirmed
/// against a real Amcrest IP5M-B1276EW-AI, whose H.265 Main/Sub1 profiles report no
/// VideoEncoderConfiguration detail at all (not Encoding, not Resolution) either from GetProfiles
/// or from a direct GetVideoEncoderConfiguration(token) call — a firmware gap, not a client bug —
/// while its H.264 Sub2 profile reports full detail. Ranking by resolution alone would have picked
/// the lower-quality H.264 sub-stream as "Main".
///
/// So profile name is checked first (vendors overwhelmingly name profiles Main/Sub[N]/Third or
/// similar — this camera does), resolution is the fallback for profiles the name heuristic can't
/// place, and original GetProfiles order is the final tiebreaker (ONVIF devices conventionally list
/// the main profile first).
/// </summary>
public static class CameraProfileRanker
{
    public static IReadOnlyList<OnvifMediaProfile> Rank(IReadOnlyList<OnvifMediaProfile> profiles, int take = 3)
        => profiles
            .Select((p, idx) => (Profile: p, Idx: idx))
            .OrderByDescending(x => NameRank(x.Profile.Name))
            .ThenByDescending(x => (long)(x.Profile.Width ?? 0) * (x.Profile.Height ?? 0))
            .ThenBy(x => x.Idx)
            .Take(take)
            .Select(x => x.Profile)
            .ToList();

    private static readonly System.Text.RegularExpressions.Regex StreamIndexPattern =
        new(@"(?:sub|stream)\D*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static int NameRank(string? name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        if (Contains(name, "main")) return 100;

        // Names like "SubStream1"/"SubStream2" don't contain the literal substring "sub2" — the
        // digit is separated by "Stream" — so pull out whatever number follows "sub"/"stream" and
        // rank by it directly (lower number = closer to main) instead of pattern-matching whole
        // words per tier.
        var match = StreamIndexPattern.Match(name);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var streamIndex))
            return 50 - streamIndex;

        if (Contains(name, "third") || Contains(name, "low")) return 1;
        if (Contains(name, "sub")) return 2; // e.g. plain "Sub" with no trailing index
        return 0;
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
