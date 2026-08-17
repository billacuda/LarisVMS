namespace LarisVMS.Onvif.Clients;

// HasAudioOutputConfig: an AudioOutputConfiguration on the profile is the practical signal that
// the camera supports two-way talk (audio backchannel) on this profile.
//
// VideoSourceToken (VideoSourceConfiguration/SourceToken) identifies which *physical* video source —
// sensor/lens — a profile draws from. Single-sensor cameras report one token across every profile,
// so it's effectively unused there; a multi-sensor device (an Axis quad-lens, say) reports a distinct
// token per lens, which is the only thing in a GetProfiles response that distinguishes "the main
// stream of lens 3" from "the main stream of lens 1". Nullable: not every firmware includes a
// VideoSourceConfiguration on every profile, and a missing token must degrade to today's
// single-channel behavior rather than dropping the profile.
public record OnvifMediaProfile(
    string Token, string? Name,
    string? VideoEncoding, int? Width, int? Height, int? FrameRateLimit, int? BitrateLimitKbps,
    string? AudioEncoding, bool HasAudio, bool HasAudioOutputConfig,
    string? VideoSourceToken = null);
