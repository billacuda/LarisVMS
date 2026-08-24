namespace LarisVMS.Core.Enums;

/// <summary>A node's chosen AI-detection hardware accelerator (object detection plan decision 2) —
/// a per-node setting (Node.AiAccelerator), not per-camera. Maps to which
/// LarisVMS.Vision.Service.&lt;Accel&gt; build variant the node runs: Nvidia -> Cuda,
/// Intel -> OpenVino (Intel's own optimized path), Amd -> DirectML (the general DX12 path, also
/// the fallback for any other DX12 GPU not otherwise covered). Auto resolves to the best
/// accelerator actually detected on the node (priority Nvidia > Intel > Amd); Cpu is offered but
/// never auto-selected by Auto — an explicit admin choice only, since silently burning CPU on
/// real-time inference on a node that wasn't provisioned for it is worse than just not running it.</summary>
public enum AiAccelerator
{
    Auto = 0,
    Nvidia = 1,
    Intel = 2,
    Amd = 3,
    Cpu = 4
}
