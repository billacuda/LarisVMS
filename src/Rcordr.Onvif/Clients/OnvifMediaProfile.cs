namespace Rcordr.Onvif.Clients;

// HasAudioOutputConfig: an AudioOutputConfiguration on the profile is the practical signal that
// the camera supports two-way talk (audio backchannel) on this profile.
public record OnvifMediaProfile(
    string Token, string? Name,
    string? VideoEncoding, int? Width, int? Height, int? FrameRateLimit, int? BitrateLimitKbps,
    string? AudioEncoding, bool HasAudio, bool HasAudioOutputConfig);
