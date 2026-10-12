using System.Buffers.Binary;
using AtmCash.Application.Abstractions;
using AtmCash.Application.Withdrawals;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Withdrawals;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Infrastructure.Persistence;

public sealed class AtmDbContext(DbContextOptions<AtmDbContext> options, TimeProvider timeProvider)
    : DbContext(options), IAtmDbContext
{
    public DbSet<Atm> Atms => Set<Atm>();

    public DbSet<CassetteMovement> Movements => Set<CassetteMovement>();

    public DbSet<WithdrawalQuote> Quotes => Set<WithdrawalQuote>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public Task LockAtmAsync(Guid atmId, CancellationToken cancellationToken) =>
        Database.ExecuteSqlAsync($"SELECT 1 FROM atms WHERE id = {atmId} FOR UPDATE", cancellationToken);

    public Task LockCardAsync(string cardHash, CancellationToken cancellationToken)
    {
        // Advisory locks take a bigint key; the first 8 bytes of the keyed hash are uniformly distributed.
        var key = BinaryPrimitives.ReadInt64BigEndian(Convert.FromHexString(cardHash.AsSpan(0, 16)));
        return Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }

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
        configurationBuilder.Properties<QuoteStatus>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<QuoteOption>().HaveConversion<string>().HaveMaxLength(16);
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
