using LarisVMS.Core;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Infrastructure.Services;

/// <inheritdoc cref="IEventColorService"/>
public class EventColorService(ISettingsResolver settings) : IEventColorService
{
    public async Task<EventPalette> GetAsync(CancellationToken ct = default)
    {
        try
        {
            var detections = new Dictionary<DetectionKind, string>();
            foreach (var kind in DetectionDisplay.AllKinds)
            {
                var stored = EventColors.Normalize(await settings.GetRawAsync(EventColors.DetectionKey(kind), ct: ct));
                // Only real overrides go in the dictionary — an absent key means "use the built-in",
                // so changing a default later still reaches every deployment that never customized it.
                if (stored is not null) detections[kind] = stored;
            }

            return new EventPalette(
                EventColors.Normalize(await settings.GetRawAsync(EventColors.MotionKey, ct: ct)),
                EventColors.Normalize(await settings.GetRawAsync(EventColors.RecordingKey, ct: ct)),
                detections);
        }
        catch
        {
            // Same stance as BrandingService: colours are cosmetic, and a timeline that renders in
            // default colours is far better than a page that fails because the Settings table isn't
            // reachable yet (pre-setup, or a transient database blip).
            return EventPalette.Empty;
        }
    }

    public async Task SaveAsync(EventPalette palette, string? modifiedBy, CancellationToken ct = default)
    {
        // Normalized here rather than trusting the caller — this is the only write path, so
        // validating at the boundary guarantees nothing unvalidated can reach storage. Empty string
        // clears a key back to the built-in default.
        await settings.SetGlobalAsync(EventColors.MotionKey, EventColors.Normalize(palette.Motion) ?? "", modifiedBy, ct);
        await settings.SetGlobalAsync(EventColors.RecordingKey, EventColors.Normalize(palette.Recording) ?? "", modifiedBy, ct);

        foreach (var kind in DetectionDisplay.AllKinds)
        {
            palette.Detections.TryGetValue(kind, out var hex);
            await settings.SetGlobalAsync(EventColors.DetectionKey(kind), EventColors.Normalize(hex) ?? "", modifiedBy, ct);
        }
    }
}
