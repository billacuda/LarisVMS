using System.Text.Json;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Infrastructure.Email.OAuth;

/// <summary>Ported from rsolva's GoogleOAuthConnectProvider (src/rsolva.Infrastructure/Email/OAuth) —
/// deliberately no Google SDK dependency, just the two raw HTTP calls the authorization-code flow
/// needs. access_type=offline + prompt=consent are both required to get a refresh token back at all:
/// without prompt=consent, a user who previously granted this app access gets silently re-consented
/// with no refresh_token in the response.</summary>
public class GoogleOAuthConnectProvider : IOAuthConnectProvider
{
    public EmailProviderType ProviderType => EmailProviderType.Gmail;

    public string BuildAuthorizationUrl(string clientId, string redirectUri, string state) =>
        "https://accounts.google.com/o/oauth2/v2/auth" +
        $"?client_id={Uri.EscapeDataString(clientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        "&response_type=code" +
        $"&scope={Uri.EscapeDataString("https://mail.google.com/")}" +
        "&access_type=offline" +
        "&prompt=consent" +
        $"&state={Uri.EscapeDataString(state)}";

    public async Task<string> ExchangeCodeForRefreshTokenAsync(string clientId, string clientSecret, string code,
        string redirectUri, CancellationToken ct = default)
    {
        using var http = new HttpClient();
        var response = await http.PostAsync("https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code"
            }), ct);
        response.EnsureSuccessStatusCode();

        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
        return json.TryGetProperty("refresh_token", out var refreshToken) && refreshToken.GetString() is { } value
            ? value
            : throw new InvalidOperationException(
                "Google did not return a refresh token. Revoke LarisVMS's access at " +
                "https://myaccount.google.com/permissions and connect again.");
    }
}
