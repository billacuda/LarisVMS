using System.Text.Json;
using Azure.Identity;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;

namespace LarisVMS.Infrastructure.Email.Providers;

/// <summary>Ported from rsolva's GraphEmailProvider (src/rsolva.Infrastructure/Email/Providers), send-
/// only — LarisVMS only ever sends alert email, unlike rsolva's ticketing inbox, so the
/// FetchNewMessages/MarkSeen half of rsolva's version has no equivalent here. App-only auth
/// (ClientSecretCredential) rather than per-user OAuth, so there's no refresh token to manage.</summary>
public class GraphEmailProvider : IEmailProvider
{
    public EmailProviderType ProviderType => EmailProviderType.Graph;

    public async Task SendAsync(string configJson, string fromAddress, string fromName, string to, string subject,
        string htmlBody, string? textBody = null, CancellationToken ct = default)
    {
        var config = JsonSerializer.Deserialize<GraphEmailConfig>(configJson)
            ?? throw new InvalidOperationException("Graph configuration is missing or malformed.");

        var credential = new ClientSecretCredential(config.TenantId, config.ClientId, config.ClientSecret);
        var client = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
        var mailbox = config.SharedMailbox ?? fromAddress;

        var message = new Message
        {
            Subject = subject,
            Body = new ItemBody { ContentType = BodyType.Html, Content = htmlBody },
            ToRecipients = [new Recipient { EmailAddress = new EmailAddress { Address = to } }],
            From = new Recipient { EmailAddress = new EmailAddress { Address = mailbox, Name = fromName } }
        };

        await client.Users[mailbox].SendMail.PostAsync(new SendMailPostRequestBody { Message = message }, cancellationToken: ct);
    }
}
