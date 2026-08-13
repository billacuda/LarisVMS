using NidusVMS.Core.Dtos;
using NidusVMS.Core.Entities;

namespace NidusVMS.Core.Interfaces;

/// <summary>M8 pass 8: CRUD for EventTagRule, plus the "probed" observed-topics browser the rule
/// editor's Start/Stop topic pickers are populated from.</summary>
public interface IEventTagRuleService
{
    Task<List<EventTagRule>> ListAsync(Guid cameraId, CancellationToken ct = default);
    Task<EventTagRule?> GetAsync(Guid id, CancellationToken ct = default);

    Task<EventTagRule> CreateAsync(Guid cameraId, string name, string startTopic, string? stopTopic,
        string colorHex, bool drivesRecording, CancellationToken ct = default);

    Task UpdateAsync(Guid id, string name, string startTopic, string? stopTopic,
        string colorHex, bool drivesRecording, bool isEnabled, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Every distinct ONVIF topic this camera's CameraEvents history has actually reported,
    /// most-recently-seen first — what the rule editor's topic pickers are built from instead of a
    /// hand-typed guess.</summary>
    Task<List<ObservedTopicDto>> ListObservedTopicsAsync(Guid cameraId, CancellationToken ct = default);
}
