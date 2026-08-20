using System.Text.Json;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace LarisVMS.Infrastructure.Email.Providers;

/// <summary>Ported from rsolva's GmailEmailProvider (src/rsolva.Infrastructure/Email/Providers), send-
/// only — same reasoning as GraphEmailProvider's own doc comment. No Google SDK dependency: the
/// access-token refresh is a plain HttpClient POST, and the send itself is MailKit's SmtpClient
/// authenticated with SaslMechanismOAuth2, exactly like rsolva. The access token is never cached —
/// exchanged fresh from the stored refresh token on every send, since a send happens rarely enough
/// (an alert, a test) that caching would only add a second failure mode for no real benefit.</summary>
public class GmailEmailProvider : IEmailProvider
{
    public EmailProviderType ProviderType => EmailProviderType.Gmail;

    public async Task SendAsync(string configJson, string fromAddress, string fromName, string to, string subject,
        string htmlBody, string? textBody = null, CancellationToken ct = default)
    {
        var config = JsonSerializer.Deserialize<GmailEmailConfig>(configJson)
            ?? throw new InvalidOperationException("Gmail configuration is missing or malformed.");
        var accessToken = await RefreshAccessTokenAsync(config, ct);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, config.EmailAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody, TextBody = textBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync("smtp.gmail.com", 587, SecureSocketOptions.StartTls, ct);
        await client.AuthenticateAsync(new SaslMechanismOAuth2(config.EmailAddress, accessToken), ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }

    private static async Task<string> RefreshAccessTokenAsync(GmailEmailConfig config, CancellationToken ct)
    {
        using var http = new HttpClient();
        var response = await http.PostAsync("https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = config.ClientId,
                ["client_secret"] = config.ClientSecret,
                ["refresh_token"] = config.RefreshToken,
                ["grant_type"] = "refresh_token"
            }), ct);
        response.EnsureSuccessStatusCode();

        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
        return json.TryGetProperty("access_token", out var token) && token.GetString() is { } value
            ? value
            : throw new InvalidOperationException("Gmail token refresh returned no access_token.");
    }
}
