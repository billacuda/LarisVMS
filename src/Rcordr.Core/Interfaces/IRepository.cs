namespace Rcordr.Core.Interfaces;

public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(object id, CancellationToken ct = default);
    Task<List<T>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    void Update(T entity);
    void Remove(T entity);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public interface IAuditService
{
    Task LogAsync(string action, string? userId, string? userName, string? ipAddress,
        string? details = null, CancellationToken ct = default);
}

public interface IPermissionService
{
    Task<bool> HasPermissionAsync(string userId, string resource, string action, CancellationToken ct = default);
}

public interface ISetupService
{
    Task<bool> IsSetupCompleteAsync(CancellationToken ct = default);
    Task<bool> IsDatabaseConfiguredAsync(CancellationToken ct = default);
    Task<bool> IsAdminCreatedAsync(CancellationToken ct = default);

    Task SetupDatabaseAsync(string serverName, string databaseName, string? username, string? password,
        CancellationToken ct = default);

    Task CompleteSetupAsync(string adminEmail, string adminPassword, CancellationToken ct = default);

    Task SaveBrandingAsync(string appName, string primaryColor, CancellationToken ct = default);

    /// <summary>Local disk path or UNC share the recorder nodes should write recordings to.
    /// Wired to actual StorageTargets once that entity lands in M4 — for M1 this is a single
    /// Settings row (Storage.RootPath) so the wizard shape matches the plan.</summary>
    Task SaveStorageRootAsync(string rootPath, CancellationToken ct = default);

    /// <summary>Generates (or returns the existing) node registration key so the wizard can print
    /// the install-node.ps1 command line. The Nodes table itself lands in M3.</summary>
    Task<string> GetOrCreateNodeRegistrationKeyAsync(CancellationToken ct = default);

    Task FinalizeSetupAsync(CancellationToken ct = default);

    Task<(string? FullPath, string? ContentType)> GetLogoInfoAsync();
}

/// <summary>
/// Walks Camera → group ancestors (nearest first) → Global → compiled-in default, memory-cached
/// with invalidation on write. Every feature that is "global with per-camera override"
/// (retention, recording mode, motion sensitivity, ...) reads through this rather than querying
/// Settings/SettingOverride directly. See CHANGELOG / plan for the resolution order.
/// </summary>
public interface ISettingsResolver
{
    Task<string?> GetRawAsync(string key, Guid? cameraId = null, CancellationToken ct = default);
    Task<T> GetAsync<T>(string key, T defaultValue, Guid? cameraId = null, CancellationToken ct = default);
    Task SetGlobalAsync(string key, string value, string? modifiedBy = null, CancellationToken ct = default);
    Task InvalidateAsync();
}
