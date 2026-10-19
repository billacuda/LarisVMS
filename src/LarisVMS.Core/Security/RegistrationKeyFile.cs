namespace LarisVMS.Core.Security;

/// <summary>
/// %ProgramData%\LarisVMS\registration.key: where the installers leave the one-time registration key
/// for a node's or proxy's first start, instead of on the service command line (whose registry entry
/// every local user can read). Read once, deleted once registered.
/// </summary>
public static class RegistrationKeyFile
{
    public static string Path { get; } = System.IO.Path.Combine(LocalDataAcl.Root, "registration.key");

    public static string? Read()
    {
        try
        {
            return File.Exists(Path) && File.ReadAllText(Path).Trim() is { Length: > 0 } key ? key : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Delete()
    {
        try { File.Delete(Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* removed on the next start */ }
    }
}
