using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace LarisVMS.Web.Security;

/// <summary>
/// This request's script nonce. Every inline &lt;script&gt; carries it (<c>nonce="@Csp.Value"</c>), so the
/// strict Content-Security-Policy (script-src 'self' 'nonce-…', no 'unsafe-inline') lets this site's
/// own inline scripts run and nothing injected into a page. Scoped: one per request.
/// </summary>
public sealed class CspNonce
{
    public string Value { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    /// <summary>Settings → Security switch: enforce the strict script policy. Off, it's sent
    /// report-only, so anything it would block is reported (<see cref="CspReports"/>) instead.</summary>
    public const string StrictSettingKey = "Security.StrictScriptPolicy";
}

/// <summary>The most recent Content-Security-Policy violation reports (POST /api/csp-report), kept in
/// memory for Settings → Security, so an admin can see whether the strict policy would break anything
/// before turning it on.</summary>
public sealed class CspReports
{
    public sealed record Entry(DateTime AtUtc, string Directive, string Blocked, string Source);

    private const int Keep = 50;
    private readonly ConcurrentQueue<Entry> _entries = new();
    private long _total;

    public long Total => Interlocked.Read(ref _total);

    /// <summary>Newest first.</summary>
    public IReadOnlyList<Entry> Recent() => _entries.Reverse().ToList();

    public void Add(Entry entry)
    {
        Interlocked.Increment(ref _total);
        _entries.Enqueue(entry);
        while (_entries.Count > Keep)
            if (!_entries.TryDequeue(out _)) break;
    }
}
