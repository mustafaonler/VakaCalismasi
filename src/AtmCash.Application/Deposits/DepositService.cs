using AtmCash.Application.Abstractions;
using AtmCash.Application.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Deposits;

namespace AtmCash.Application.Deposits;

public sealed record CurrencyAmountDto(Currency Currency, long Amount);

/// <summary>
/// Outcome of a deposit: where every accepted note went and which notes were handed back.
/// </summary>
public sealed record DepositResultDto(
    Guid DepositId,
    Guid AtmId,
    IReadOnlyList<DepositPlacement> Accepted,
    IReadOnlyList<ReturnedNotes> Returned,
    IReadOnlyList<CurrencyAmountDto> AcceptedTotals,
    int AcceptedNoteCount,
    int ReturnedNoteCount,
    DateTimeOffset CreatedAt);

/// <summary>Customer deposits (Part A #11, B7).</summary>
public sealed class DepositService(IAtmDbContext db, TimeProvider timeProvider)
{
    /// <summary>
    /// Runs under the ATM row lock like every other cash change, so parallel deposits and
    /// withdrawals on one ATM never see stale free space; balances and DEPOSIT movements are
    /// committed in one transaction.
    /// </summary>
    public Task<DepositResultDto> DepositAsync(
        Guid atmId, IReadOnlyList<DepositNote> notes, int unrecognisedCount, CancellationToken cancellationToken) =>
        db.ChangeAtmAsync(
            atmId,
            atm =>
            {
                var receipt = atm.Deposit(Guid.CreateVersion7(), notes, unrecognisedCount);
                return Task.FromResult(new DepositResultDto(
                    receipt.DepositId,
                    atm.Id,
                    receipt.Placements,
                    receipt.Returned,
                    receipt.Placements
                        .GroupBy(p => p.Currency)
                        .OrderBy(g => g.Key)
                        .Select(g => new CurrencyAmountDto(g.Key, g.Sum(p => (long)p.Denomination * p.Count)))
                        .ToList(),
                    receipt.Placements.Sum(p => p.Count),
                    receipt.Returned.Sum(r => r.Count),
                    timeProvider.GetUtcNow()));
            },
            cancellationToken);
}
