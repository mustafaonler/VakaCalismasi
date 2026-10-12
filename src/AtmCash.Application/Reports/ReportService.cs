using AtmCash.Application.Abstractions;
using AtmCash.Application.Common;
using AtmCash.Application.Reconciliations;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Application.Reports;

public sealed record ReportHoldingDto(Currency Currency, int Denomination, int NoteCount, int ReservedCount, int AvailableCount);

public sealed record ReportCassetteDto(
    int Slot,
    CassetteKind Kind,
    Currency? Currency,
    int? Denomination,
    int Capacity,
    int NoteCount,
    int ReservedCount,
    int AvailableCount,
    bool IsLowCash,
    IReadOnlyList<ReportHoldingDto> Notes);

/// <summary>Totals of one currency. Amounts count every physical note; <c>AvailableAmount</c> excludes reserved notes.</summary>
public sealed record CurrencyTotalDto(
    Currency Currency,
    int NoteCount,
    int ReservedCount,
    int AvailableCount,
    decimal Amount,
    decimal AvailableAmount,
    decimal RateToTry,
    decimal AmountTry);

public sealed record AtmReportDto(
    Guid AtmId,
    string Code,
    AtmStatus Status,
    Guid? PendingReconciliationId,
    IReadOnlyList<ReportCassetteDto> Cassettes,
    IReadOnlyList<CurrencyTotalDto> Totals,
    decimal TotalTry);

public sealed record OperationsReportDto(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ExchangeRateDto> ExchangeRates,
    IReadOnlyList<AtmReportDto> Atms,
    IReadOnlyList<CurrencyTotalDto> Totals,
    decimal TotalTry);

/// <summary>Operations report per ATM and cassette with totals per currency and in TRY (B10).</summary>
public sealed class ReportService(IAtmDbContext db, TimeProvider timeProvider)
{
    public async Task<OperationsReportDto> GetOperationsReportAsync(Guid? atmId, CancellationToken cancellationToken)
    {
        var query = db.Atms.AsNoTracking().Include(a => a.Cassettes).AsQueryable();
        if (atmId is { } id)
        {
            query = query.Where(a => a.Id == id);
        }

        var atms = await query.OrderBy(a => a.Code).ToListAsync(cancellationToken);
        if (atmId is not null && atms.Count == 0)
        {
            throw DomainException.NotFound(ErrorCodes.AtmNotFound, $"ATM {atmId} was not found.");
        }

        var rates = await db.ExchangeRates.AsNoTracking().ToDictionaryAsync(r => r.Currency, r => r.RateToTry, cancellationToken);

        var reports = atms.Select(atm =>
        {
            var holdings = atm.Cassettes.SelectMany(c => c.Holdings).ToList();
            var totals = Totals(holdings, rates);
            return new AtmReportDto(
                atm.Id,
                atm.Code,
                atm.Status,
                atm.PendingReconciliationId,
                atm.Cassettes.OrderBy(c => c.Slot).Select(ToReport).ToList(),
                totals,
                Money.Round(totals.Sum(t => t.AmountTry)));
        }).ToList();

        var networkTotals = Totals(atms.SelectMany(a => a.Cassettes).SelectMany(c => c.Holdings).ToList(), rates);
        return new OperationsReportDto(
            timeProvider.GetUtcNow(),
            rates.OrderBy(r => r.Key).Select(r => new ExchangeRateDto(r.Key, r.Value)).ToList(),
            reports,
            networkTotals,
            Money.Round(networkTotals.Sum(t => t.AmountTry)));
    }

    private static ReportCassetteDto ToReport(Cassette cassette) => new(
        cassette.Slot,
        cassette.Kind,
        cassette.Currency,
        cassette.Denomination,
        Cassette.Capacity,
        cassette.Count,
        cassette.Reserved,
        cassette.Available,
        cassette.IsLowOnCash,
        cassette.Holdings
            .Where(h => h.Count > 0)
            .OrderBy(h => h.Currency).ThenByDescending(h => h.Denomination)
            .Select(h => new ReportHoldingDto(h.Currency, h.Denomination, h.Count, h.Reserved, h.Available))
            .ToList());

    private static List<CurrencyTotalDto> Totals(List<CassetteHolding> holdings, Dictionary<Currency, decimal> rates) =>
        holdings
            .Where(h => h.Count > 0)
            .GroupBy(h => h.Currency)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var amount = g.Sum(h => (decimal)h.Denomination * h.Count);
                var rate = rates.TryGetValue(g.Key, out var r) ? r : throw new InvalidOperationException($"No exchange rate for {g.Key}.");
                return new CurrencyTotalDto(
                    g.Key,
                    g.Sum(h => h.Count),
                    g.Sum(h => h.Reserved),
                    g.Sum(h => h.Available),
                    Money.Round(amount),
                    Money.Round(g.Sum(h => (decimal)h.Denomination * h.Available)),
                    rate,
                    Money.Round(amount * rate));
            })
            .ToList();
}
