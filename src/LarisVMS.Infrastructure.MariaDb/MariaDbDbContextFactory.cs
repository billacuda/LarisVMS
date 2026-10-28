using LarisVMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LarisVMS.Infrastructure.MariaDb;

/// <summary>
/// Lets `dotnet ef` build the MariaDB model with this project as both --project and --startup-project
/// (see tools\add-migration.ps1). Never connects: the server version is fixed (MariaDbOps.ServerVersion),
/// so generating a migration needs no MariaDB server. Not used at runtime.
/// </summary>
public class MariaDbDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>();
        MariaDbOps.Instance.Configure(options, "Server=localhost;Database=larisvms;User ID=larisvms");
        return new ApplicationDbContext(options.Options);
    }
}
