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
}
