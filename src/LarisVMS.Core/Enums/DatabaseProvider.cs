namespace LarisVMS.Core.Enums;

/// <summary>Which database server holds the app's data — Database:Provider in setup-generated.json.
/// Missing means SQL Server (every install before MariaDB support). MariaDB is experimental.</summary>
public enum DatabaseProvider
{
    SqlServer = 0,
    MariaDb = 1
}
