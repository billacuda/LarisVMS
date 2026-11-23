using LarisVMS.Core.Setup;

namespace LarisVMS.Tests;

/// <summary>The pieces of <c>larisvms-node setup</c> / <c>larisvms-proxy setup</c> that don't need
/// root: the env file the systemd unit reads, and argument checking.</summary>
public class LinuxSetupTests
{
    private static readonly HashSet<string> Options = ["--server-url", "--registration-key", "--server-cert-thumbprint", "--live-port"];
    private static readonly HashSet<string> Flags = ["--reregister"];

    [Fact]
    public void MergeEnv_KeepsCommentsAndOtherKeys_ReplacesAndAdds()
    {
        var existing = "# LarisVMS Node settings\nLARISVMS_SERVER_URL=\"https://old:8444\"\nLARISVMS_LIVE_PORT=\"8554\"\n";

        var merged = LinuxSetup.MergeEnv(existing, new Dictionary<string, string?>
        {
            ["LARISVMS_SERVER_URL"] = "https://new:8444",
            ["LARISVMS_STORAGE_ROOT"] = "/mnt/rec",
        });

        Assert.Equal("# LarisVMS Node settings\nLARISVMS_SERVER_URL=\"https://new:8444\"\nLARISVMS_LIVE_PORT=\"8554\"\n" +
                     "LARISVMS_STORAGE_ROOT=\"/mnt/rec\"\n", merged);
    }

    [Fact]
    public void MergeEnv_NullRemovesAKey()
    {
        var merged = LinuxSetup.MergeEnv("A=\"1\"\nB=\"2\"\n", new Dictionary<string, string?> { ["A"] = null });
        Assert.Equal("B=\"2\"\n", merged);
    }

    [Fact]
    public void Values_AreQuotedForSystemd_AndReadBack()
    {
        var merged = LinuxSetup.MergeEnv(null, new Dictionary<string, string?> { ["P"] = "/mnt/my \"rec\" \\x" });

        Assert.Equal("P=\"/mnt/my \\\"rec\\\" \\\\x\"\n", merged);
        Assert.Equal("/mnt/my \"rec\" \\x", LinuxSetup.ReadEnv(merged, "P"));
        Assert.Equal("8554", LinuxSetup.ReadEnv("LARISVMS_LIVE_PORT=8554", "LARISVMS_LIVE_PORT"));
        Assert.Null(LinuxSetup.ReadEnv("# LARISVMS_LIVE_PORT=1", "LARISVMS_LIVE_PORT"));
    }

    [Fact]
    public void ALineBreakInAValue_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => LinuxSetup.MergeEnv(null, new Dictionary<string, string?> { ["A"] = "x\nEVIL=1" }));
        Assert.NotNull(SetupArgs.Parse(["--server-url", "https://a\nb"], Options, Flags).Error);
    }

    [Fact]
    public void Args_ParseOptionsAndFlags()
    {
        var parsed = SetupArgs.Parse(["--server-url", "https://vms:8444", "--reregister", "--live-port", "9000"], Options, Flags);

        Assert.Null(parsed.Error);
        Assert.Equal("https://vms:8444", parsed.Value("--server-url"));
        Assert.True(parsed.Has("--reregister"));
        Assert.True(parsed.TryPort("--live-port", out var port, out _));
        Assert.Equal(9000, port);
        Assert.Null(parsed.ValidateCommon());
    }

    [Theory]
    [InlineData(new[] { "--bogus" }, "Unknown option --bogus.")]
    [InlineData(new[] { "--server-url" }, "--server-url needs a value.")]
    [InlineData(new[] { "--server-url", "--reregister" }, "--server-url needs a value.")]
    public void Args_RejectUnknownAndMissingValues(string[] args, string error) =>
        Assert.Equal(error, SetupArgs.Parse(args, Options, Flags).Error);

    [Fact]
    public void Args_ValidateTheServerAddressThumbprintAndPorts()
    {
        Assert.NotNull(SetupArgs.Parse(["--server-url", "vms:8444"], Options, Flags).ValidateCommon());
        Assert.NotNull(SetupArgs.Parse(["--server-url", "ftp://vms"], Options, Flags).ValidateCommon());
        Assert.NotNull(SetupArgs.Parse(["--server-cert-thumbprint", "abc"], Options, Flags).ValidateCommon());
        Assert.Null(SetupArgs.Parse(["--server-cert-thumbprint", new string('a', 64)], Options, Flags).ValidateCommon());
        Assert.False(SetupArgs.Parse(["--live-port", "70000"], Options, Flags).TryPort("--live-port", out _, out var error));
        Assert.Contains("1 to 65535", error);
    }
}
