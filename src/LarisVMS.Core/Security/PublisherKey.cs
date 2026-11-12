namespace LarisVMS.Core.Security;

/// <summary>
/// The publisher's update-signing key, kept offline by whoever builds releases. Its public half is
/// compiled into the node and proxy (embedded from <c>Security/publisher-key.pem</c> when that file
/// exists at build time; <c>tools/publisher-key.ps1 -New</c> makes it). A node or proxy built with it
/// installs an update only if the publisher signed it too (<see cref="UpdateSignature.UpdatePayload"/>,
/// signed by <c>build-node.ps1</c> / <c>build-proxy.ps1</c> into <see cref="SignatureFileName"/>), on top
/// of the server's own signature. So a compromised server can't push its own code to nodes: it holds
/// the server's key but never the publisher's.
///
/// Built without the file (from source, or before a key was made), there is no publisher check and
/// the server's signature alone decides, as before.
/// </summary>
public static class PublisherKey
{
    public const string ResourceName = "LarisVMS.PublisherKey.pem";

    /// <summary>Written beside the build's version file in the publish folder, and read when the
    /// build is registered.</summary>
    public const string SignatureFileName = "publisher.sig";

    public static string? PublicKeyPem { get; } = Load();

    private static string? Load()
    {
        using var stream = typeof(PublisherKey).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        var pem = reader.ReadToEnd().Trim();
        return pem.Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal) ? pem : null;
    }

    /// <summary>Whether an update passes the publisher check with the built-in key.</summary>
    public static bool Check(string payload, string? signature, out string error) =>
        Check(PublicKeyPem, payload, signature, out error);

    public static bool Check(string? publicKeyPem, string payload, string? signature, out string error)
    {
        error = "";
        if (publicKeyPem is null) return true;
        if (string.IsNullOrEmpty(signature))
        {
            error = "it isn't signed by the publisher";
            return false;
        }
        if (!UpdateSignature.Verify(publicKeyPem, payload, signature))
        {
            error = "the publisher's signature doesn't match";
            return false;
        }
        return true;
    }
}
