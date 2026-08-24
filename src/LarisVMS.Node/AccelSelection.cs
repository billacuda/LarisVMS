using LarisVMS.Core.Enums;

namespace LarisVMS.Node;

/// <summary>
/// Resolves a node's desired AiAccelerator (Node.AiAccelerator, delivered node-side via
/// NodeConfigResponse.AiAccelerator) against whatever AccelCapabilityProber actually detected on
/// this machine — object detection plan decision 2. Mirrors EncoderSelection.ChooseH264Encoder's
/// shape exactly: a small, pure, priority-ordered choice with no I/O of its own.
/// </summary>
public static class AccelSelection
{
    /// <summary>Priority order Auto resolves through — richest/most mature execution provider
    /// first. Cpu is deliberately absent: Auto never silently falls back to burning CPU on
    /// real-time inference on a node that wasn't provisioned for it (see AiAccelerator's own doc
    /// comment) — an explicit Cpu choice is the only way to get it.</summary>
    private static readonly AiAccelerator[] AutoPriority = [AiAccelerator.Nvidia, AiAccelerator.Intel, AiAccelerator.Amd];

    /// <summary>Returns the accelerator this node should actually use, or null if none is
    /// available — Auto with nothing detected, or an explicit non-Cpu choice whose hardware isn't
    /// present. Null means "don't start a Vision Service instance at all" (see NodeWorker's own
    /// accelerator-resolution logic, once it exists) — every detection path a camera already has
    /// configured (ONVIF, vendor CGI, ServerMotion) is completely unaffected either way.</summary>
    public static AiAccelerator? Choose(AiAccelerator desired, IReadOnlyList<AiAccelerator> detected)
    {
        if (desired == AiAccelerator.Cpu) return AiAccelerator.Cpu; // always resolvable — no hardware detection needed to run on CPU

        if (desired == AiAccelerator.Auto)
        {
            foreach (var candidate in AutoPriority)
            {
                if (detected.Contains(candidate)) return candidate;
            }
            return null;
        }

        // An explicit non-Cpu choice is only honored if that specific hardware was actually detected.
        return detected.Contains(desired) ? desired : null;
    }
}
