using AtmCash.Application.Abstractions;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Application.Atms;

internal static class AtmQueries
{
    /// <summary>Loads the ATM aggregate with its cassettes and holdings.</summary>
    public static async Task<Atm> LoadAtmAsync(
        this IAtmDbContext db, Guid atmId, bool tracking, CancellationToken cancellationToken)
    {
        var query = db.Atms.Include(a => a.Cassettes).AsQueryable();
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query.SingleOrDefaultAsync(a => a.Id == atmId, cancellationToken)
            ?? throw DomainException.NotFound(ErrorCodes.AtmNotFound, $"ATM {atmId} was not found.");
    }

    /// <summary>
    /// Runs a change on one ATM inside a transaction that holds the ATM's row lock. Cassette
    /// balances and the movements they produce are saved together, or not at all.
    /// </summary>
    public static async Task<T> ChangeAtmAsync<T>(
        this IAtmDbContext db, Guid atmId, Func<Atm, Task<T>> change, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.LockAtmAsync(atmId, cancellationToken);
        var atm = await db.LoadAtmAsync(atmId, tracking: true, cancellationToken);

        var result = await change(atm);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
