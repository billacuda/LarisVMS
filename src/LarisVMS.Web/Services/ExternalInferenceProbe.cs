using System.Net.Http.Headers;
using System.Text.Json;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Web.Services;

/// <summary>
/// The "Test connection" button on the AI-detection settings page: hits an external inference
/// service's <c>GET /healthz</c> and <c>GET /v1/models</c> so an operator can confirm it is up and
/// pick a model without leaving the page. Never throws — every failure comes back as
/// <see cref="ExternalInferenceProbeResult.Error"/>, the same nullable-<c>Error</c> convention
/// <c>CameraService.ProbeAsync</c> already uses for the ONVIF "test" button.
///
/// The response parsing is split out into <see cref="BuildResult"/> (pure, no HTTP) so it is
/// unit-testable against canned JSON the way every other parsing helper in this codebase is.
/// </summary>
public sealed class ExternalInferenceProbe(IHttpClientFactory httpClientFactory, ILogger<ExternalInferenceProbe> logger)
{
    public const string HttpClientName = "external-inference";

    /// <param name="apiKey">Sent as <c>Authorization: Bearer {apiKey}</c> on both probe requests —
    /// null/blank for a service that needs no auth (e.g. SideGlance bound to loopback only).</param>
    public async Task<ExternalInferenceProbeResult> ProbeAsync(string baseUrl, string? apiKey, CancellationToken ct)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) || root.Scheme is not ("http" or "https"))
            return new ExternalInferenceProbeResult("Enter a full service URL like http://192.168.1.50:8080.");

        var client = httpClientFactory.CreateClient(HttpClientName);
        var authHeader = string.IsNullOrWhiteSpace(apiKey) ? null : new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        string? healthJson;
        string? modelsJson;
        try
        {
            healthJson = await GetStringOrNullAsync(client, new Uri(root, "healthz"), authHeader, ct);
            modelsJson = await GetStringAsync(client, new Uri(root, "v1/models"), authHeader, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "External inference probe of {BaseUrl} failed.", baseUrl);
            return new ExternalInferenceProbeResult($"Could not reach the service at {baseUrl}: {ex.Message}");
        }

        return BuildResult(healthJson, modelsJson);
    }

    /// <summary>Pure: turns the two response bodies (<paramref name="modelsJson"/> is required,
    /// <paramref name="healthJson"/> optional — an older/leaner service may not expose
    /// <c>/healthz</c>) into the settings-page result. A models body that doesn't parse, or parses to
    /// an empty list, is an error — there is nothing to pick.</summary>
    public static ExternalInferenceProbeResult BuildResult(string? healthJson, string? modelsJson)
    {
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        ExternalHealthResponse? health = null;
        if (!string.IsNullOrWhiteSpace(healthJson))
        {
            try { health = JsonSerializer.Deserialize<ExternalHealthResponse>(healthJson, json); }
            catch (JsonException) { /* health is best-effort context, not the point of the probe */ }
        }

        if (string.IsNullOrWhiteSpace(modelsJson))
            return new ExternalInferenceProbeResult("The service returned no model list.", health);

        List<ExternalModelInfo>? models;
        try
        {
            // Tolerate both { "models": [...] } and a bare [...] top-level array.
            models = modelsJson.TrimStart().StartsWith('[')
                ? JsonSerializer.Deserialize<List<ExternalModelInfo>>(modelsJson, json)
                : JsonSerializer.Deserialize<ExternalModelsResponse>(modelsJson, json)?.Models?.ToList();
        }
        catch (JsonException ex)
        {
            return new ExternalInferenceProbeResult($"The service's model list did not parse: {ex.Message}", health);
        }

        if (models is null || models.Count == 0)
            return new ExternalInferenceProbeResult("The service reported no models.", health);

        return new ExternalInferenceProbeResult(null, health, models);
    }

    private static async Task<string> GetStringAsync(HttpClient client, Uri uri, AuthenticationHeaderValue? authHeader, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Headers = { Authorization = authHeader } };
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static async Task<string?> GetStringOrNullAsync(HttpClient client, Uri uri, AuthenticationHeaderValue? authHeader, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Headers = { Authorization = authHeader } };
            using var response = await client.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
