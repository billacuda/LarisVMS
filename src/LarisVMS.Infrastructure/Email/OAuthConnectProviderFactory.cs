using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Infrastructure.Email;

/// <summary>Mirrors EmailProviderFactory — dictionary-maps every DI-registered IOAuthConnectProvider
/// by its ProviderType.</summary>
public class OAuthConnectProviderFactory(IEnumerable<IOAuthConnectProvider> providers)
{
    private readonly IReadOnlyDictionary<EmailProviderType, IOAuthConnectProvider> _map =
        providers.ToDictionary(p => p.ProviderType);

    public IOAuthConnectProvider Get(EmailProviderType type) =>
        _map.TryGetValue(type, out var provider)
            ? provider
            : throw new InvalidOperationException($"No IOAuthConnectProvider is registered for '{type}'.");
}
