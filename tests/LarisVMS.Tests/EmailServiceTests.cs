using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Email;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

public class EmailServiceTests
{
    private static (ApplicationDbContext Db, EmailService Service) NewService()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);
        var factory = new EmailProviderFactory([]);
        return (db, new EmailService(db, factory));
    }

    [Fact]
    public async Task NoRowAtAllMeansNotConfigured()
    {
        var (_, service) = NewService();
        Assert.False(await service.IsConfiguredAsync());
    }

    [Fact]
    public async Task ARowThatsDisabledIsNotConfigured()
    {
        var (db, service) = NewService();
        db.EmailSettings.Add(new EmailSettings
        {
            Id = Guid.NewGuid(), IsEnabled = false, Provider = EmailProviderType.Smtp,
            FromAddress = "alerts@example.com", FromName = "LarisVMS", SmtpHost = "smtp.example.com"
        });
        await db.SaveChangesAsync();

        Assert.False(await service.IsConfiguredAsync());
    }

    [Fact]
    public async Task AnEnabledRowWithAHostAndFromAddressIsConfigured()
    {
        var (db, service) = NewService();
        db.EmailSettings.Add(new EmailSettings
        {
            Id = Guid.NewGuid(), IsEnabled = true, Provider = EmailProviderType.Smtp,
            FromAddress = "alerts@example.com", FromName = "LarisVMS", SmtpHost = "smtp.example.com"
        });
        await db.SaveChangesAsync();

        Assert.True(await service.IsConfiguredAsync());
    }

    [Fact]
    public async Task SendingWithNoSettingsRowThrows()
    {
        var (_, service) = NewService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync("to@example.com", "subject", "<p>body</p>"));
    }

    [Fact]
    public async Task SendingWhileDisabledThrows()
    {
        var (db, service) = NewService();
        db.EmailSettings.Add(new EmailSettings
        {
            Id = Guid.NewGuid(), IsEnabled = false, Provider = EmailProviderType.Smtp,
            FromAddress = "alerts@example.com", FromName = "LarisVMS", SmtpHost = "smtp.example.com"
        });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync("to@example.com", "subject", "<p>body</p>"));
    }

    [Fact]
    public async Task AnEnabledGraphRowWithTenantClientAndSecretIsConfigured()
    {
        var (db, service) = NewService();
        db.EmailSettings.Add(new EmailSettings
        {
            Id = Guid.NewGuid(), IsEnabled = true, Provider = EmailProviderType.Graph,
            FromAddress = "alerts@example.com", FromName = "LarisVMS",
            GraphTenantId = "tenant", GraphClientId = "client", GraphClientSecret = "secret"
        });
        await db.SaveChangesAsync();

        Assert.True(await service.IsConfiguredAsync());
    }

    [Fact]
    public async Task AGraphRowMissingAClientSecretIsNotConfigured()
    {
        var (db, service) = NewService();
        db.EmailSettings.Add(new EmailSettings
        {
            Id = Guid.NewGuid(), IsEnabled = true, Provider = EmailProviderType.Graph,
            FromAddress = "alerts@example.com", FromName = "LarisVMS",
            GraphTenantId = "tenant", GraphClientId = "client"
        });
        await db.SaveChangesAsync();

        Assert.False(await service.IsConfiguredAsync());
    }

    [Fact]
    public async Task AnEnabledGmailRowWithAllFourFieldsIsConfigured()
    {
        var (db, service) = NewService();
        db.EmailSettings.Add(new EmailSettings
        {
            Id = Guid.NewGuid(), IsEnabled = true, Provider = EmailProviderType.Gmail,
            FromAddress = "alerts@example.com", FromName = "LarisVMS",
            GmailClientId = "client", GmailClientSecret = "secret",
            GmailRefreshToken = "refresh-token", GmailEmailAddress = "alerts@gmail.com"
        });
        await db.SaveChangesAsync();

        Assert.True(await service.IsConfiguredAsync());
    }

    [Fact]
    public async Task AGmailRowThatHasntCompletedTheOAuthConnectIsNotConfigured()
    {
        // Client ID/Secret can be saved before the admin ever clicks "connect" — GmailRefreshToken is
        // only ever written by the OAuth callback, so this is the state right after saving the form
        // but before consent.
        var (db, service) = NewService();
        db.EmailSettings.Add(new EmailSettings
        {
            Id = Guid.NewGuid(), IsEnabled = true, Provider = EmailProviderType.Gmail,
            FromAddress = "alerts@example.com", FromName = "LarisVMS",
            GmailClientId = "client", GmailClientSecret = "secret", GmailEmailAddress = "alerts@gmail.com"
        });
        await db.SaveChangesAsync();

        Assert.False(await service.IsConfiguredAsync());
    }
}
