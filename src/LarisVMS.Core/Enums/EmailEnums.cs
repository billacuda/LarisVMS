namespace LarisVMS.Core.Enums;

/// <summary>Which outbound provider EmailSettings is configured for. Only Smtp has a working
/// IEmailProvider today (M15 pass 1) — Graph and Gmail are reserved for the OAuth2 passes that
/// follow, so the admin page only offers Smtp for now even though the column can hold either value
/// once those providers exist.</summary>
public enum EmailProviderType
{
    Smtp = 0,
    Graph = 1,
    Gmail = 2
}
