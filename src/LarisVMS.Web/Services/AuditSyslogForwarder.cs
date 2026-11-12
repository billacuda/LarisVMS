using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// Sends every new audit log entry to a syslog server (Settings → Logs), so a copy lives somewhere
/// an attacker on this server or its database can't reach. RFC 5424 messages, facility "log audit",
/// with the entry as JSON (which also keeps anything in it from breaking the line). UDP, TCP, or TLS;
/// TCP and TLS use octet-counted framing (RFC 6587 / RFC 5425).
///
/// Works from a cursor (<see cref="AuditChainState.ForwardedThroughId"/>) rather than a queue: if the
/// syslog server is down, or this service restarts, it carries on from the last entry it sent.
/// Turning forwarding on starts from the newest entry, not the whole history; turning it off clears
/// the cursor.
/// </summary>
public sealed class AuditSyslogForwarder(IServiceScopeFactory scopeFactory, ILogger<AuditSyslogForwarder> logger) : BackgroundService
{
    public const string HostKey = "AuditLog.Syslog.Host";
    public const string PortKey = "AuditLog.Syslog.Port";
    public const string ProtocolKey = "AuditLog.Syslog.Protocol";
    public static readonly string[] Protocols = ["udp", "tcp", "tls"];

    public static int DefaultPort(string protocol) => protocol == "tls" ? 6514 : 514;

    /// <summary>Largest UDP message: well under any network's datagram limit. Longer details are cut.</summary>
    internal const int MaxUdpBytes = 8000;
    private const int MaxStreamBytes = 64 * 1024;
    private const int BatchSize = 200;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    public sealed record Target(string Host, int Port, string Protocol);

    /// <summary>For Settings → Logs: when the last entry went out, and the last error since.</summary>
    public static DateTime? LastSentUtc { get; private set; }
    public static string? LastError { get; private set; }

    public static async Task<Target?> ReadTargetAsync(ISettingsResolver settings, CancellationToken ct = default)
    {
        var host = (await settings.GetRawAsync(HostKey, ct: ct))?.Trim();
        if (string.IsNullOrEmpty(host)) return null;
        var protocol = await settings.GetAsync(ProtocolKey, "udp", ct: ct);
        if (!Protocols.Contains(protocol)) protocol = "udp";
        var port = await settings.GetAsync(PortKey, 0, ct: ct);
        return new Target(host, port is > 0 and <= 65535 ? port : DefaultPort(protocol), protocol);
    }

    /// <summary>A host name, IPv4 address, or IPv6 address, without a port.</summary>
    public static bool IsValidHost(string host) =>
        host.Contains(':')
            ? System.Net.IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            : host.Length is > 0 and <= 253 && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private Sender? _sender;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    wait = await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (LastError is null) logger.LogWarning(ex, "Could not forward audit log entries to syslog; retrying.");
                    LastError = ex.Message;
                    await CloseAsync();
                    wait = RetryInterval;
                }

                if (wait > TimeSpan.Zero)
                {
                    try { await Task.Delay(wait, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            await CloseAsync();
        }
    }

    private async Task CloseAsync()
    {
        if (_sender is not null) await _sender.DisposeAsync();
        _sender = null;
    }

    private async Task<TimeSpan> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var target = await ReadTargetAsync(settings, ct);
        var state = await db.AuditChainStates.SingleOrDefaultAsync(s => s.Id == AuditChainState.SingletonId, ct);

        // Turned off, or pointed somewhere else: entries written before the change (the settings
        // change itself included) still go to the destination that was set when they were written.
        if (_sender is { } previous && previous.Destination != target && state?.ForwardedThroughId is { } through)
            await FlushToPreviousAsync(db, state, previous, through, ct);

        if (target is null)
        {
            await CloseAsync();
            if (state?.ForwardedThroughId is not null)
            {
                state.ForwardedThroughId = null;
                await db.SaveChangesAsync(ct);
            }
            LastError = null;
            return IdleInterval;
        }
        if (state is null) return PollInterval; // nothing written since the chain started

        if (state.ForwardedThroughId is null)
        {
            state.ForwardedThroughId = await db.AuditLogs.MaxAsync(l => (long?)l.Id, ct) ?? 0;
            await db.SaveChangesAsync(ct);
            return TimeSpan.Zero;
        }

        if (_sender?.Destination != target)
        {
            await CloseAsync();
            _sender = new Sender(target);
        }
        var sender = _sender;

        var cursor = state.ForwardedThroughId.Value;
        var batch = await db.AuditLogs.AsNoTracking().Where(l => l.Id > cursor).OrderBy(l => l.Id)
            .Take(BatchSize).ToListAsync(ct);
        if (batch.Count == 0) return PollInterval;

        var host = HostName();
        try
        {
            foreach (var entry in batch)
            {
                var max = target.Protocol == "udp" ? MaxUdpBytes : MaxStreamBytes;
                await sender.SendAsync(Format(entry, host, max), ct);
                cursor = entry.Id;
            }
        }
        finally
        {
            if (cursor != state.ForwardedThroughId)
            {
                state.ForwardedThroughId = cursor;
                await db.SaveChangesAsync(CancellationToken.None);
                LastSentUtc = DateTime.UtcNow;
            }
        }
        if (LastError is not null) logger.LogInformation("Forwarding audit log entries to syslog again.");
        LastError = null;
        return batch.Count == BatchSize ? TimeSpan.Zero : PollInterval;
    }

    /// <summary>Sends whatever is left (up to <see cref="FlushLimit"/> entries) to the destination
    /// that's being replaced. Best effort: a failure is logged and forwarding moves on.</summary>
    private async Task FlushToPreviousAsync(ApplicationDbContext db, AuditChainState state, Sender previous, long through, CancellationToken ct)
    {
        var pending = await db.AuditLogs.AsNoTracking().Where(l => l.Id > through).OrderBy(l => l.Id)
            .Take(FlushLimit).ToListAsync(ct);
        if (pending.Count == 0) return;
        var host = HostName();
        var cursor = through;
        try
        {
            foreach (var entry in pending)
            {
                await previous.SendAsync(Format(entry, host, previous.Destination.Protocol == "udp" ? MaxUdpBytes : MaxStreamBytes), ct);
                cursor = entry.Id;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send the last audit log entries to the previous syslog destination.");
        }
        if (cursor != through)
        {
            state.ForwardedThroughId = cursor;
            await db.SaveChangesAsync(CancellationToken.None);
            LastSentUtc = DateTime.UtcNow;
        }
    }

    private const int FlushLimit = 1000;

    private static string HostName()
    {
        var name = new string(Environment.MachineName.Where(c => c is > ' ' and < (char)127).ToArray());
        return name.Length == 0 ? "-" : name;
    }

    /// <summary>One RFC 5424 message. Details are shortened, if needed, to stay within <paramref name="maxBytes"/>.</summary>
    public static string Format(AuditLog entry, string hostName, int maxBytes)
    {
        const int Priority = 13 * 8 + 5; // facility 13 (log audit), severity 5 (notice)
        var time = DateTime.SpecifyKind(entry.OccurredAt, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        var details = entry.Details;
        while (true)
        {
            var json = JsonSerializer.Serialize(new
            {
                id = entry.Id,
                time,
                action = entry.Action,
                userId = entry.UserId,
                user = entry.UserName,
                ip = entry.IpAddress,
                details,
                hash = entry.Hash,
            });
            var message = $"<{Priority}>1 {time} {hostName} LarisVMS - audit - {json}";
            var length = Encoding.UTF8.GetByteCount(message);
            if (length <= maxBytes || string.IsNullOrEmpty(details)) return message;
            var keep = Math.Max(0, details.Length - Math.Max(16, (length - maxBytes) / 2 + 16));
            details = keep == 0 ? "…" : details[..keep] + "…";
        }
    }

    /// <summary>TCP and TLS framing: the message's length in bytes, a space, then the message.</summary>
    public static byte[] Frame(string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var prefix = Encoding.ASCII.GetBytes(body.Length.ToString(CultureInfo.InvariantCulture) + " ");
        return [.. prefix, .. body];
    }

    private sealed class Sender(Target destination) : IAsyncDisposable
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
        private UdpClient? _udp;
        private TcpClient? _tcp;
        private Stream? _stream;

        public Target Destination { get; } = destination;

        public async Task SendAsync(string message, CancellationToken ct)
        {
            if (Destination.Protocol == "udp")
            {
                _udp ??= new UdpClient();
                await _udp.SendAsync(Encoding.UTF8.GetBytes(message), Destination.Host, Destination.Port, ct);
                return;
            }

            if (_stream is null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ConnectTimeout);
                _tcp = new TcpClient();
                await _tcp.ConnectAsync(Destination.Host, Destination.Port, timeout.Token);
                Stream stream = _tcp.GetStream();
                if (Destination.Protocol == "tls")
                {
                    // The server's certificate must be trusted by this machine and name this host.
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = Destination.Host }, timeout.Token);
                    stream = ssl;
                }
                _stream = stream;
            }
            await _stream.WriteAsync(Frame(message), ct);
            await _stream.FlushAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (_stream is not null) await _stream.DisposeAsync();
            _tcp?.Dispose();
            _udp?.Dispose();
        }
    }
}
