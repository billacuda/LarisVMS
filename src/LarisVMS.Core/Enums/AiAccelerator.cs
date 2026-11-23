namespace LarisVMS.Core.Enums;

/// <summary>A node's chosen AI-detection hardware accelerator (object detection plan decision 2) —
/// a per-node setting (Node.AiAccelerator), not per-camera. Maps to which
/// LarisVMS.Vision.Service.&lt;Accel&gt; build variant the node runs: Nvidia -> Cuda,
/// Amd -> DirectML (the general DX12 path, also the fallback for any other DX12 GPU not otherwise
/// covered). Auto resolves to the best accelerator actually detected on the node (priority
/// Nvidia > Intel > Amd); Cpu is offered but never auto-selected by Auto — an explicit admin choice
/// only, since silently burning CPU on real-time inference on a node that wasn't provisioned for it
/// is worse than just not running it.
///
/// <see cref="Intel"/> is what Auto resolves to on an Intel GPU, and is kept for wire/DB back-compat
/// with an already-persisted node. It runs Intel's OpenVINO plugin, the same as <see cref="OpenVino"/>
/// (it used to silently resolve to DirectML, which is now only its fallback on Windows); it is no
/// longer offered in the Admin UI: pick <see cref="OpenVino"/> or <see cref="Amd"/> (DirectML, works
/// on any DX12 GPU on Windows) instead.
///
/// <see cref="TensorRt"/> and <see cref="OpenVino"/> are additive (not renames) so a node running an
/// older build during a rolling upgrade can't misparse a value it doesn't recognize as anything worse
/// than a silent fall-back to <see cref="Auto"/> (see AiAccelerator's wire/DB usage in
/// NodeConfigResponse/NodeService). <see cref="TensorRt"/> resolves like <see cref="Nvidia"/>
/// (same CUDA native build) but additionally turns on TensorRT for every local engine, not just
/// D-FINE (see VisionServiceSupervisor.SetEnableTensorRt). <see cref="OpenVino"/> forces the
/// (already-implemented but previously UI-unreachable) OpenVINO backend explicitly, rather than the
/// old Intel-labelled value silently running DirectML instead.
///
/// <see cref="MIGraphX"/> is a placeholder for AMD's newer-generation accelerator (native Windows
/// support exists upstream) — offered in the dropdown so it's visible as a future option, but
/// deliberately never implemented here: <c>LarisVMS.Node.AccelSelection.Choose</c> always resolves
/// it to <c>null</c> (no accelerator, no Vision Service), and the Admin UI shows an inline
/// "future feature" notice instead of any real backend behavior.</summary>
public enum AiAccelerator
{
    Auto = 0,
    Nvidia = 1,
    Intel = 2,
    Amd = 3,
    Cpu = 4,
    TensorRt = 5,
    OpenVino = 6,
    MIGraphX = 7,
}
