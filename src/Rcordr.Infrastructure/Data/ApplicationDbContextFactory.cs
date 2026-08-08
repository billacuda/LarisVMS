using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Rcordr.Infrastructure.Data;

/// <summary>
/// Lets `dotnet ef` construct the DbContext without a running app / configured connection string.
/// Hardcoded local dev connection — never used at runtime, only by design-time tooling
/// (migrations add / database update without --connection).
/// </summary>
public class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=.;Database=Rcordr;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        return new ApplicationDbContext(options);
    }
}
