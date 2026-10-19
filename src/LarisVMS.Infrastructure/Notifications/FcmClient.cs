using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LarisVMS.Infrastructure.Notifications;

/// <summary>
/// Firebase Cloud Messaging, HTTP v1 API, with the admin's own Firebase project. The OAuth access
/// token comes from the project's service account: an RS256 JWT signed here with the account's
/// private key and exchanged at Google's token endpoint (cached until shortly before it expires),
/// so no Google SDK is needed. Messages are data-only — LarisVMS's own service worker shows them,
/// the same way it shows a standard Web Push message.
/// </summary>
public sealed class FcmClient(Func<HttpClient> httpClientFactory)
{
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
    private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTime _cachedTokenExpiresUtc;
    private string? _cachedFor;

    public sealed record ServiceAccount(
        [property: JsonPropertyName("project_id")] string ProjectId,
        [property: JsonPropertyName("client_email")] string ClientEmail,
        [property: JsonPropertyName("private_key")] string PrivateKey,
        [property: JsonPropertyName("token_uri")] string? TokenUri);

    public enum SendResult { Sent, TokenGone, Failed }

    /// <summary>Parses and checks a pasted service account JSON. Throws a readable message when it
    /// isn't one.</summary>
    public static ServiceAccount ParseServiceAccount(string json)
    {
        ServiceAccount? account;
        try { account = JsonSerializer.Deserialize<ServiceAccount>(json); }
        catch (JsonException) { throw new ArgumentException("That isn't valid JSON. Paste the whole service account key file."); }
        if (account is null || string.IsNullOrWhiteSpace(account.ProjectId) || string.IsNullOrWhiteSpace(account.ClientEmail)
            || string.IsNullOrWhiteSpace(account.PrivateKey))
            throw new ArgumentException("The service account JSON needs project_id, client_email, and private_key.");
        using var rsa = RSA.Create();
        try { rsa.ImportFromPem(account.PrivateKey); }
        catch (Exception) { throw new ArgumentException("The service account's private_key couldn't be read."); }
        return account;
    }

    /// <summary>Sends one notification to one FCM registration token.</summary>
    public async Task<(SendResult Result, string? Error)> SendAsync(ServiceAccount account, string registrationToken,
        IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var accessToken = await GetAccessTokenAsync(account, ct);
        var body = JsonSerializer.Serialize(new
        {
            message = new
            {
                token = registrationToken,
                data,
                webpush = new { headers = new Dictionary<string, string> { ["TTL"] = "86400", ["Urgency"] = "high" } },
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(account.ProjectId)}/messages:send")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClientFactory().SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return (SendResult.Sent, null);

        var text = await response.Content.ReadAsStringAsync(ct);
        // UNREGISTERED / NOT_FOUND: the browser dropped the registration — forget it.
        if (response.StatusCode == HttpStatusCode.NotFound || text.Contains("UNREGISTERED", StringComparison.Ordinal))
            return (SendResult.TokenGone, null);
        return (SendResult.Failed, $"FCM returned {(int)response.StatusCode}: {Truncate(text)}");
    }

    private async Task<string> GetAccessTokenAsync(ServiceAccount account, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            var cacheKey = account.ClientEmail + "|" + account.ProjectId;
            if (_cachedToken is not null && _cachedFor == cacheKey && DateTime.UtcNow < _cachedTokenExpiresUtc)
                return _cachedToken;

            var tokenUri = string.IsNullOrWhiteSpace(account.TokenUri) ? DefaultTokenUri : account.TokenUri;
            var assertion = BuildAssertion(account, tokenUri, DateTime.UtcNow);
            using var response = await httpClientFactory().PostAsync(tokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion,
            }), ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Google rejected the service account ({(int)response.StatusCode}): {Truncate(text)}");

            using var doc = JsonDocument.Parse(text);
            _cachedToken = doc.RootElement.GetProperty("access_token").GetString()!;
            var lifetime = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            _cachedTokenExpiresUtc = DateTime.UtcNow.AddSeconds(lifetime - 300);
            _cachedFor = cacheKey;
            return _cachedToken;
        }
        finally { _tokenLock.Release(); }
    }

    /// <summary>The signed JWT exchanged for an access token (internal for tests).</summary>
    internal static string BuildAssertion(ServiceAccount account, string tokenUri, DateTime nowUtc)
    {
        var header = WebPushProtocol.Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var iat = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
        var claims = WebPushProtocol.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = account.ClientEmail,
            ["scope"] = Scope,
            ["aud"] = tokenUri,
            ["iat"] = iat,
            ["exp"] = iat + 3600,
        }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(account.PrivateKey);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(header + "." + claims), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{claims}.{WebPushProtocol.Base64Url(signature)}";
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
