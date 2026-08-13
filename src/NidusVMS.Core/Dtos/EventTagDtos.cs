namespace NidusVMS.Core.Dtos;

// M8 pass 8: EventTagRule CRUD + the "probed" observed-topics browser the rule editor picks
// Start/Stop topics from, instead of the user having to hand-type an ONVIF topic string.

/// <summary>Body for both create and update, same shape either way — see SaveZoneRequest's own
/// doc comment for why this is the established convention in this codebase.</summary>
public record SaveEventTagRuleRequest(
    string Name, string StartTopic, string? StopTopic, string ColorHex, bool DrivesRecording, bool IsEnabled = true);

public record EventTagRuleDto(
    Guid Id, Guid CameraId, string Name, string StartTopic, string? StopTopic,
    string ColorHex, bool DrivesRecording, bool IsEnabled);

/// <summary>One distinct ONVIF topic this camera has actually reported at least once, with enough
/// context (a real sample payload, how often, how recently) for a user to recognize what it is
/// without needing to already know ONVIF's topic-naming conventions. This is the "probe" — built
/// from this camera's own CameraEvents history rather than a live GetEventProperties capability
/// query, since real observed activity is both simpler to build (no new ONVIF client call) and more
/// trustworthy than a device's advertised-but-not-necessarily-accurate topic set.</summary>
public record ObservedTopicDto(string Topic, int Count, DateTime FirstSeenUtc, DateTime LastSeenUtc, string? SamplePayloadJson);
