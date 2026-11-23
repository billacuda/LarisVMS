using System.Globalization;
using LarisVMS.Core.Security;

namespace LarisVMS.Core.Setup;

/// <summary>"--name value" and "--flag" arguments for <c>larisvms-node setup</c> and
/// <c>larisvms-proxy setup</c>, checked against what each command accepts.</summary>
public sealed class SetupArgs
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public string? Error { get; private init; }

    public static SetupArgs Parse(string[] args, IReadOnlySet<string> options, IReadOnlySet<string> flags)
    {
        var result = new SetupArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (flags.Contains(name)) result._values[name] = null;
            else if (options.Contains(name) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                result._values[name] = args[++i];
            else
                return new SetupArgs { Error = options.Contains(name) ? $"{name} needs a value." : $"Unknown option {name}." };
        }
        foreach (var (name, value) in result._values)
        {
            if (value is not null && (value.Contains('\n') || value.Contains('\r')))
                return new SetupArgs { Error = $"{name} can't contain a line break." };
        }
        return result;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Value(string name) => _values.TryGetValue(name, out var value) ? value : null;

    /// <summary>The checks both commands share: the server address and the pinned thumbprint.</summary>
    public string? ValidateCommon()
    {
        if (Value("--server-url") is { } url
            && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
            return "--server-url must be an http(s) address, e.g. https://vms.example.com:8444";
        if (Value("--server-cert-thumbprint") is { } thumbprint && ServerCertificateTrust.FromArgs(thumbprint).Error is { } error)
            return error;
        return null;
    }

    public bool TryPort(string name, out int? port, out string error)
    {
        port = null;
        error = "";
        if (Value(name) is not { } raw) return true;
        if (int.TryParse(raw, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and < 65536)
        {
            port = parsed;
            return true;
        }
        error = $"{name} must be a port number from 1 to 65535.";
        return false;
    }
}
