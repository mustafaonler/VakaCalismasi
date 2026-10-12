using AtmCash.Application.Abstractions;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Application.Consistency;

/// <summary>Stored figures of one cassette holding next to the figures replayed from its movements.</summary>
public sealed record HoldingConsistencyDto(
    Currency Currency,
    int Denomination,
    int StoredCount,
    int LedgerCount,
    int CountDifference,
    int StoredReserved,
    int LedgerReserved,
    int ReservedDifference,
    bool IsConsistent);

public sealed record CassetteConsistencyDto(
    int Slot, CassetteKind Kind, bool IsConsistent, IReadOnlyList<HoldingConsistencyDto> Holdings);

public sealed record AtmConsistencyDto(
    Guid AtmId, string Code, bool IsConsistent, int DifferenceCount, IReadOnlyList<CassetteConsistencyDto> Cassettes);

public sealed record ConsistencyReportDto(
    DateTimeOffset CheckedAt,
    bool IsConsistent,
    int AtmCount,
    int InconsistentAtmCount,
    IReadOnlyList<AtmConsistencyDto> Atms);

/// <summary>
/// Compares the stored cassette balances with the balances reproduced from the movement ledger (B8).
/// Note counts are the sum of every non-reservation movement; reserved counts are the sum of
/// RESERVATION and RESERVATION_RELEASE movements.
/// </summary>
public sealed class ConsistencyService(IAtmDbContext db, TimeProvider timeProvider)
{
    public async Task<ConsistencyReportDto> CheckAsync(Guid? atmId, CancellationToken cancellationToken)
    {
        // Both reads see one snapshot, so a withdrawal committing in between cannot show up as a false difference.
        await using var transaction = await db.BeginSnapshotTransactionAsync(cancellationToken);

        var atmQuery = db.Atms.AsNoTracking().Include(a => a.Cassettes).AsQueryable();
        var movementQuery = db.Movements.AsQueryable();
        if (atmId is { } id)
        {
            atmQuery = atmQuery.Where(a => a.Id == id);
            movementQuery = movementQuery.Where(m => m.AtmId == id);
        }

        var atms = await atmQuery.OrderBy(a => a.Code).ToListAsync(cancellationToken);
        if (atmId is not null && atms.Count == 0)
        {
            throw DomainException.NotFound(ErrorCodes.AtmNotFound, $"ATM {atmId} was not found.");
        }

        var ledger = (await movementQuery
                .GroupBy(m => new { m.CassetteId, m.Currency, m.Denomination })
                .Select(g => new
                {
                    g.Key.CassetteId,
                    g.Key.Currency,
                    g.Key.Denomination,
                    Count = g.Sum(m =>
                        m.Type == MovementType.Reservation || m.Type == MovementType.ReservationRelease ? 0 : m.Quantity),
                    Reserved = g.Sum(m =>
                        m.Type == MovementType.Reservation || m.Type == MovementType.ReservationRelease ? m.Quantity : 0),
                })
                .ToListAsync(cancellationToken))
            .ToLookup(l => l.CassetteId);

        await transaction.CommitAsync(cancellationToken);

        var reports = atms.Select(atm =>
        {
            var cassettes = atm.Cassettes.OrderBy(c => c.Slot).Select(cassette =>
            {
                var stored = cassette.Holdings.ToDictionary(h => (h.Currency, h.Denomination), h => (h.Count, h.Reserved));
                var replayed = ledger[cassette.Id].ToDictionary(l => (l.Currency, l.Denomination), l => (l.Count, l.Reserved));
                var holdings = stored.Keys.Union(replayed.Keys)
                    .Select(k =>
                    {
                        var (count, reserved) = stored.GetValueOrDefault(k);
                        var (ledgerCount, ledgerReserved) = replayed.GetValueOrDefault(k);
                        return new HoldingConsistencyDto(
                            k.Currency, k.Denomination,
                            count, ledgerCount, count - ledgerCount,
                            reserved, ledgerReserved, reserved - ledgerReserved,
                            count == ledgerCount && reserved == ledgerReserved);
                    })
                    // Holdings emptied long ago net to zero on both sides; they add nothing to the report.
                    .Where(h => h.StoredCount != 0 || h.LedgerCount != 0 || h.StoredReserved != 0 || h.LedgerReserved != 0)
                    .OrderBy(h => h.Currency).ThenByDescending(h => h.Denomination)
                    .ToList();
                return new CassetteConsistencyDto(cassette.Slot, cassette.Kind, holdings.All(h => h.IsConsistent), holdings);
            }).ToList();

            var differences = cassettes.Sum(c => c.Holdings.Count(h => !h.IsConsistent));
            return new AtmConsistencyDto(atm.Id, atm.Code, differences == 0, differences, cassettes);
        }).ToList();

        return new ConsistencyReportDto(
            timeProvider.GetUtcNow(),
            reports.All(r => r.IsConsistent),
            reports.Count,
            reports.Count(r => !r.IsConsistent),
            reports);
    }
}
