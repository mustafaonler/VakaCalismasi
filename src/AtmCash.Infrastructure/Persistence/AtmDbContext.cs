using AtmCash.Application.Abstractions;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Infrastructure.Persistence;

public sealed class AtmDbContext(DbContextOptions<AtmDbContext> options, TimeProvider timeProvider)
    : DbContext(options), IAtmDbContext
{
    public DbSet<Atm> Atms => Set<Atm>();

    public DbSet<CassetteMovement> Movements => Set<CassetteMovement>();

    public Task LockAtmAsync(Guid atmId, CancellationToken cancellationToken) =>
        Database.ExecuteSqlAsync($"SELECT 1 FROM atms WHERE id = {atmId} FOR UPDATE", cancellationToken);

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        CollectDomainRecords();
        return await base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        CollectDomainRecords();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AtmDbContext).Assembly);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are stored as readable strings rather than ordinals.
        configurationBuilder.Properties<AtmStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<CassetteKind>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<Currency>().HaveConversion<string>().HaveMaxLength(3);
        configurationBuilder.Properties<MovementType>().HaveConversion<string>().HaveMaxLength(32);
    }

    /// <summary>Moves ledger entries raised by aggregates into the same unit of work.</summary>
    private void CollectDomainRecords()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var atm in ChangeTracker.Entries<Atm>().Select(e => e.Entity).ToList())
        {
            foreach (var movement in atm.DequeueNewMovements())
            {
                var entry = Movements.Add(movement);
                entry.Property(m => m.CreatedAt).CurrentValue = now;
            }
        }
    }
}
