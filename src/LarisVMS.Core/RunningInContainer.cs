namespace LarisVMS.Core;

/// <summary>Whether this process runs from one of the LarisVMS Docker images, which set
/// <c>LARISVMS_CONTAINER=1</c>. A container is updated by pulling a new image, so nodes and proxies
/// in one report it on their heartbeat, and the server offers them no build to install.</summary>
public static class RunningInContainer
{
    public const string Variable = "LARISVMS_CONTAINER";

    public static bool Value { get; } = Environment.GetEnvironmentVariable(Variable) == "1";
}
