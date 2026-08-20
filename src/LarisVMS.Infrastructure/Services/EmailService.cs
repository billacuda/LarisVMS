using System.Text.Json;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;

namespace LarisVMS.Infrastructure.Services;

/// <summary>EmailSettings is a singleton row (one deployment, one outbound sender) — GetSettingsAsync
/// just takes the first one rather than needing an Id, same shape as a real single-row config table
/// would use ("the" row instead of "a" row).</summary>
public class EmailService(ApplicationDbContext db, EmailProviderFactory providerFactory) : IEmailService
{
    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
    {
        var s = await GetSettingsAsync(ct);
        if (s is not { IsEnabled: true } || string.IsNullOrWhiteSpace(s.FromAddress)) return false;

        return s.Provider switch
        {
            EmailProviderType.Smtp => !string.IsNullOrWhiteSpace(s.SmtpHost),
            EmailProviderType.Graph => !string.IsNullOrWhiteSpace(s.GraphTenantId)
                && !string.IsNullOrWhiteSpace(s.GraphClientId) && !string.IsNullOrWhiteSpace(s.GraphClientSecret),
            EmailProviderType.Gmail => !string.IsNullOrWhiteSpace(s.GmailClientId) && !string.IsNullOrWhiteSpace(s.GmailClientSecret)
                && !string.IsNullOrWhiteSpace(s.GmailRefreshToken) && !string.IsNullOrWhiteSpace(s.GmailEmailAddress),
            _ => false
        };
    }

    public async Task SendAsync(string to, string subject, string htmlBody, string? textBody = null, CancellationToken ct = default)
    {
        var s = await GetSettingsAsync(ct)
            ?? throw new InvalidOperationException("Email isn't configured yet — see Admin -> Settings -> Email.");
        if (!s.IsEnabled)
            throw new InvalidOperationException("Email is configured but disabled — see Admin -> Settings -> Email.");

        var provider = providerFactory.Get(s.Provider);
        var configJson = s.Provider switch
        {
            EmailProviderType.Smtp => JsonSerializer.Serialize(new SmtpEmailConfig(
                s.SmtpHost ?? string.Empty, s.SmtpPort, s.SmtpUseSsl, s.SmtpUsername, s.SmtpPassword)),
            EmailProviderType.Graph => JsonSerializer.Serialize(new GraphEmailConfig(
                s.GraphTenantId ?? string.Empty, s.GraphClientId ?? string.Empty, s.GraphClientSecret ?? string.Empty, s.GraphSharedMailbox)),
            EmailProviderType.Gmail => JsonSerializer.Serialize(new GmailEmailConfig(
                s.GmailClientId ?? string.Empty, s.GmailClientSecret ?? string.Empty, s.GmailRefreshToken ?? string.Empty, s.GmailEmailAddress ?? string.Empty)),
            _ => throw new InvalidOperationException($"'{s.Provider}' isn't implemented yet.")
        };

        await provider.SendAsync(configJson, s.FromAddress, s.FromName, to, subject, htmlBody, textBody, ct);
    }

    private Task<Core.Entities.EmailSettings?> GetSettingsAsync(CancellationToken ct) =>
        db.EmailSettings.FirstOrDefaultAsync(ct);
}
