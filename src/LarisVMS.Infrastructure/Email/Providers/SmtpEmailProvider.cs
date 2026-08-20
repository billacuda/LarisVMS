using System.Text.Json;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace LarisVMS.Infrastructure.Email.Providers;

/// <summary>Ported from rsolva's SmtpEmailProvider (src/rsolva.Infrastructure/Email/Providers) — same
/// MailKit connect/authenticate(if credentials given)/send shape.</summary>
public class SmtpEmailProvider : IEmailProvider
{
    public EmailProviderType ProviderType => EmailProviderType.Smtp;

    public async Task SendAsync(string configJson, string fromAddress, string fromName, string to, string subject,
        string htmlBody, string? textBody = null, CancellationToken ct = default)
    {
        var config = JsonSerializer.Deserialize<SmtpEmailConfig>(configJson)
            ?? throw new InvalidOperationException("SMTP configuration is missing or malformed.");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;

        var builder = new BodyBuilder { HtmlBody = htmlBody, TextBody = textBody };
        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        var socketOptions = config.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(config.Host, config.Port, socketOptions, ct);
        if (!string.IsNullOrEmpty(config.Username))
            await client.AuthenticateAsync(config.Username, config.Password ?? string.Empty, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}
