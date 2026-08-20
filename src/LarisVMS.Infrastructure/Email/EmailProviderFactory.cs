using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Infrastructure.Email;

/// <summary>Ported from rsolva's EmailProviderFactory — dictionary-maps every DI-registered
/// IEmailProvider by its ProviderType so EmailService can resolve the one EmailSettings.Provider
/// currently names without a switch statement that needs editing each time a provider is added.</summary>
public class EmailProviderFactory(IEnumerable<IEmailProvider> providers)
{
    private readonly IReadOnlyDictionary<EmailProviderType, IEmailProvider> _map =
        providers.ToDictionary(p => p.ProviderType);

    public IEmailProvider Get(EmailProviderType type) =>
        _map.TryGetValue(type, out var provider)
            ? provider
            : throw new InvalidOperationException($"No IEmailProvider is registered for '{type}'.");
}
