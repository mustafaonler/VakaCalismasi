using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;

namespace AtmCash.Application.Atms;

public sealed record AtmSummaryDto(Guid Id, string Code, AtmStatus Status);

/// <summary>
/// Current contents of an ATM: every cassette plus note counts and balance per currency (Part A #12).
/// <c>PendingReconciliationId</c> is set while a reconciliation awaits four-eyes approval (B9).
/// </summary>
public sealed record AtmDetailsDto(
    Guid Id,
    string Code,
    AtmStatus Status,
    IReadOnlyList<CassetteDto> Cassettes,
    IReadOnlyList<CurrencyBalanceDto> Balances,
    Guid? PendingReconciliationId);

public sealed record CassetteDto(
    int Slot,
    CassetteKind Kind,
    Currency? Currency,
    int? Denomination,
    int Capacity,
    int Count,
    int Reserved,
    int Available,
    IReadOnlyList<HoldingDto> Notes);

public sealed record HoldingDto(Currency Currency, int Denomination, int Count, int Reserved);

public sealed record CurrencyBalanceDto(
    Currency Currency,
    IReadOnlyList<DenominationCountDto> Denominations,
    int NoteCount,
    long Total);

public sealed record DenominationCountDto(int Denomination, int Count, long Amount);

internal static class AtmMapping
{
    public static AtmSummaryDto ToSummary(this Atm atm) => new(atm.Id, atm.Code, atm.Status);

    public static AtmDetailsDto ToDetails(this Atm atm)
    {
        var cassettes = atm.Cassettes
            .OrderBy(c => c.Slot)
            .Select(c => new CassetteDto(
                c.Slot,
                c.Kind,
                c.Currency,
                c.Denomination,
                Cassette.Capacity,
                c.Count,
                c.Reserved,
                c.Available,
                c.Holdings
                    .Where(h => h.Count > 0)
                    .OrderBy(h => h.Currency).ThenByDescending(h => h.Denomination)
                    .Select(h => new HoldingDto(h.Currency, h.Denomination, h.Count, h.Reserved))
                    .ToList()))
            .ToList();

        var balances = atm.Cassettes
            .SelectMany(c => c.Holdings)
            .Where(h => h.Count > 0)
            .GroupBy(h => h.Currency)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var denominations = g
                    .GroupBy(h => h.Denomination)
                    .OrderByDescending(d => d.Key)
                    .Select(d => new DenominationCountDto(d.Key, d.Sum(h => h.Count), (long)d.Key * d.Sum(h => h.Count)))
                    .ToList();
                return new CurrencyBalanceDto(
                    g.Key, denominations, denominations.Sum(d => d.Count), denominations.Sum(d => d.Amount));
            })
            .ToList();

        return new AtmDetailsDto(atm.Id, atm.Code, atm.Status, cassettes, balances, atm.PendingReconciliationId);
    }
}
