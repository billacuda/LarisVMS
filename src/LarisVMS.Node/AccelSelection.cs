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

        // MIGraphX is a placeholder (AMD's next-gen accelerator) — no build work exists for it, so it
        // always resolves to "nothing" rather than silently running some other backend under a label
        // that doesn't match. The Admin UI shows a "future feature" notice for this choice instead.
        if (desired == AiAccelerator.MIGraphX) return null;

        if (desired == AiAccelerator.Auto)
        {
            foreach (var candidate in AutoPriority)
            {
                if (detected.Contains(candidate)) return candidate;
            }
            return null;
        }

        // TensorRt/OpenVino are additive refinements of the Nvidia/Intel vendor detection above, not
        // separate hardware families of their own — TensorRt needs an Nvidia GPU (same CUDA native
        // build, TensorRT layered on top via VisionServiceSupervisor.SetEnableTensorRt); OpenVino
        // needs an Intel GPU (the explicit OpenVINO backend, superseding the old Intel->DirectML bug).
        if (desired == AiAccelerator.TensorRt) return detected.Contains(AiAccelerator.Nvidia) ? AiAccelerator.TensorRt : null;
        if (desired == AiAccelerator.OpenVino) return detected.Contains(AiAccelerator.Intel) ? AiAccelerator.OpenVino : null;

        // Amd is DirectML (labelled "DirectML" in the node settings), which runs on any DX12 GPU,
        // not just AMD's. Requiring an AMD GPU meant choosing DirectML on an NVIDIA or Intel node
        // resolved to nothing, so the Vision Service never started and no camera got detections.
        if (desired == AiAccelerator.Amd)
            return detected.Any(a => a is AiAccelerator.Nvidia or AiAccelerator.Intel or AiAccelerator.Amd) ? AiAccelerator.Amd : null;

        // An explicit non-Cpu choice is only honored if that specific hardware was actually detected.
        return detected.Contains(desired) ? desired : null;
    }
}
