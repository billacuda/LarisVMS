using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Dtos;

/// <summary>What the setup wizard's Database step collects.</summary>
/// <param name="Server">SQL Server: a server or server\instance name. MariaDB: a host name or address.</param>
/// <param name="Port">MariaDB only; null means 3306.</param>
/// <param name="Username">SQL Server: blank uses the service's Windows account. MariaDB: required.</param>
/// <param name="TrustServerCertificate">Accept the server's certificate without checking it (needed
/// for a self-signed one). Always the case for SQL Server on this machine.</param>
public record DatabaseSetupRequest(
    DatabaseProvider Provider,
    string Server,
    string DatabaseName,
    string? Username,
    string? Password,
    bool TrustServerCertificate = false,
    int? Port = null);
