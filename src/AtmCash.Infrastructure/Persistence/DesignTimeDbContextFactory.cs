using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AtmCash.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef</c> only; adding migrations does not connect to the database.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AtmDbContext>
{
    public AtmDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AtmDbContext>();
        DependencyInjection.Configure(options, "Host=localhost;Database=atmcash;Username=atmcash;Password=atmcash");
        return new AtmDbContext(options.Options, TimeProvider.System);
    }
}
