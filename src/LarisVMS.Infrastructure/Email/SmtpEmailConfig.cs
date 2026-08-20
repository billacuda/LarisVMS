namespace LarisVMS.Infrastructure.Email;

/// <summary>The shape EmailService serializes EmailSettings' Smtp* columns into, and SmtpEmailProvider
/// deserializes IEmailProvider.SendAsync's configJson back into. Kept as its own small DTO (rather
/// than passing EmailSettings itself into the provider) so a provider only ever sees the fields it
/// actually needs, matching Graph/Gmail's own config shapes once those exist.</summary>
public record SmtpEmailConfig(string Host, int Port, bool UseSsl, string? Username, string? Password);
