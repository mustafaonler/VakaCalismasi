using AtmCash.Application.Outbox;
using AtmCash.Application.Withdrawals;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Reconciliations;
using AtmCash.Domain.Withdrawals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace AtmCash.Application.Abstractions;

public interface IAtmDbContext
{
    DbSet<Atm> Atms { get; }

    DbSet<CassetteMovement> Movements { get; }

    DbSet<WithdrawalQuote> Quotes { get; }

    DbSet<IdempotencyRecord> IdempotencyRecords { get; }

    DbSet<Reconciliation> Reconciliations { get; }

    DbSet<ExchangeRate> ExchangeRates { get; }

    DbSet<OutboxMessage> OutboxMessages { get; }

    DatabaseFacade Database { get; }

    /// <summary>
    /// Takes a row lock on the ATM for the rest of the current transaction (SELECT ... FOR UPDATE).
    /// Every operation that changes an ATM's cash takes this lock first, so operations on the same
    /// ATM run one at a time while different ATMs proceed in parallel.
    /// </summary>
    Task LockAtmAsync(Guid atmId, CancellationToken cancellationToken);

    /// <summary>
    /// Takes a transaction-scoped advisory lock on a card so that two quotes for the same card at
    /// different ATMs cannot both pass the daily limit check. Always taken before the ATM lock.
    /// </summary>
    Task LockCardAsync(string cardHash, CancellationToken cancellationToken);

    /// <summary>Starts a REPEATABLE READ transaction: every query in it reads the same snapshot.</summary>
    Task<IDbContextTransaction> BeginSnapshotTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Locks up to <paramref name="max"/> unprocessed outbox messages, oldest first, for the current
    /// transaction. SKIP LOCKED lets several workers run without picking the same message.
    /// </summary>
    Task<List<OutboxMessage>> LockPendingOutboxMessagesAsync(int max, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
