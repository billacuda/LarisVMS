using System.Reflection;
using System.Text.Json;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Services;

/// <summary>
/// Checks GitHub once a day — at startup, then every 24 hours — for a LarisVMS release newer than the
/// one running, and publishes it to <see cref="ReleaseCheckState"/> for the top-bar notice. This is
/// the only routine outbound call LarisVMS makes (see the README's Privacy section): a plain anonymous
/// GET of the public releases list that sends nothing about this install beyond what any HTTP request
/// carries. Off with <see cref="EnabledKey"/> (Settings → Node defaults).
///
/// Reads the releases list rather than /releases/latest, because LarisVMS ships as pre-releases
/// ("v0.213.1-beta") and /latest skips those; drafts are ignored.
/// </summary>
public sealed class ReleaseCheckService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    ReleaseCheckState state,
    ILogger<ReleaseCheckService> logger) : BackgroundService
{
    public const string EnabledKey = "ReleaseCheck.Enabled";
    public const bool DefaultEnabled = true;
    public const string HttpClientName = "github-releases";
    // Deliberately fixed: the project's own public releases, the one place this check may ever go.
#pragma warning disable S1075
    public const string ReleasesApiUrl = "https://api.github.com/repos/billacuda/LarisVMS/releases?per_page=20";
    public const string ReleasesPageUrl = "https://github.com/billacuda/LarisVMS/releases/";
#pragma warning restore S1075

    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>The version this server is running, from its own assembly ("0.214.0+sha" → 0.214.0).</summary>
    public static string? RunningVersion { get; } =
        typeof(ReleaseCheckService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ReleaseCheckService).Assembly.GetName().Version?.ToString(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            state.ResetCheckRequest();
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Offline servers are normal; keep whatever was last found and try again tomorrow.
                logger.LogInformation(ex, "Could not check GitHub for a new LarisVMS version: {Message}", ex.Message);
            }

            try { await Task.WhenAny(Task.Delay(Interval, stoppingToken), state.WaitForCheckRequestAsync()); }
            catch (OperationCanceledException) { break; }
            if (stoppingToken.IsCancellationRequested) break;
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        using (var scope = scopeFactory.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
            if (!await settings.GetAsync(EnabledKey, DefaultEnabled, ct: ct))
            {
                state.Set(null);
                return;
            }
        }

        using var http = httpClientFactory.CreateClient(HttpClientName);
        await using var body = await http.GetStreamAsync(ReleasesApiUrl, ct);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);

        var newer = FindNewerRelease(json.RootElement, RunningVersion);
        state.Set(newer);
        if (newer is not null)
            logger.LogInformation("LarisVMS {Version} is available (running {Running}): {Url}", newer.Version, RunningVersion, newer.Url);
    }

    /// <summary>The highest-versioned non-draft release in a GitHub releases array, if it's newer than
    /// <paramref name="running"/>; otherwise null. An unreadable running version never shows a notice.</summary>
    internal static AvailableRelease? FindNewerRelease(JsonElement releases, string? running)
    {
        if (releases.ValueKind != JsonValueKind.Array || !ReleaseVersion.TryParse(running, out var current)) return null;

        Version? best = null;
        string? bestUrl = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            if (!release.TryGetProperty("tag_name", out var tag) || !ReleaseVersion.TryParse(tag.GetString(), out var version)) continue;
            if (best is not null && version <= best) continue;

            best = version;
            bestUrl = release.TryGetProperty("html_url", out var url) ? url.GetString() : null;
        }

        // The link goes into every admin's top bar, so only ever point it at this repo's releases.
        if (best is null || best <= current || bestUrl is null
            || !bestUrl.StartsWith(ReleasesPageUrl, StringComparison.OrdinalIgnoreCase)) return null;
        return new AvailableRelease(best.ToString(), bestUrl);
    }
}
