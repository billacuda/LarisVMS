using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Email;

namespace LarisVMS.Tests;

public class EmailProviderFactoryTests
{
    private sealed class FakeProvider(EmailProviderType type) : IEmailProvider
    {
        public EmailProviderType ProviderType => type;
        public Task SendAsync(string configJson, string fromAddress, string fromName, string to, string subject,
            string htmlBody, string? textBody = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void ResolvesTheProviderMatchingTheRequestedType()
    {
        var factory = new EmailProviderFactory([new FakeProvider(EmailProviderType.Smtp), new FakeProvider(EmailProviderType.Graph)]);

        Assert.Equal(EmailProviderType.Smtp, factory.Get(EmailProviderType.Smtp).ProviderType);
        Assert.Equal(EmailProviderType.Graph, factory.Get(EmailProviderType.Graph).ProviderType);
    }

    [Fact]
    public void ThrowsForAProviderTypeNothingIsRegisteredFor()
    {
        var factory = new EmailProviderFactory([new FakeProvider(EmailProviderType.Smtp)]);

        Assert.Throws<InvalidOperationException>(() => factory.Get(EmailProviderType.Gmail));
    }
}
