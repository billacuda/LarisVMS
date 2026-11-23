using System.Diagnostics;
using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>How the node starts the Vision Service on Linux: the library path, and reading the child's
/// environment without throwing when a variable isn't set (an unset LD_LIBRARY_PATH under systemd
/// used to fail every reconcile once AI detection was on).</summary>
public class VisionLinuxLibraryPathTests
{
    [Fact]
    public void AnUnsetVariable_ReadsAsNull()
    {
        var psi = new ProcessStartInfo("x");
        psi.Environment.Remove("LD_LIBRARY_PATH");

        Assert.Null(VisionServiceSupervisor.EnvironmentValue(psi, "LD_LIBRARY_PATH"));
        psi.Environment["Vision__CudnnPath"] = "/opt/cudnn";
        Assert.Equal("/opt/cudnn", VisionServiceSupervisor.EnvironmentValue(psi, "Vision__CudnnPath"));
    }

    [Fact]
    public void LibraryPath_PutsTheBackendsFirst_SkipsMissingFolders_KeepsTheExistingValue()
    {
        var existing = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine("/opt/larisvms/node", "onnxruntime-backends", "openvino"),
            "/opt/cudnn",
        };

        var path = VisionServiceSupervisor.LinuxLibraryPath("/opt/larisvms/node", "/usr/lib/extra",
            ["/opt/cudnn", null, "/nope"], existing.Contains);

        Assert.Equal(string.Join(':', Path.Combine("/opt/larisvms/node", "onnxruntime-backends", "openvino"), "/opt/cudnn", "/usr/lib/extra"), path);
        Assert.Equal(string.Join(':', Path.Combine("/opt/larisvms/node", "onnxruntime-backends", "openvino")),
            VisionServiceSupervisor.LinuxLibraryPath("/opt/larisvms/node", null, [], existing.Contains));
    }
}
