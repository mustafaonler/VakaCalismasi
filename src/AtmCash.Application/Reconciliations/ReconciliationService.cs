using AtmCash.Application.Abstractions;
using AtmCash.Application.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Reconciliations;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Application.Reconciliations;

public sealed record ReconciliationLineDto(
    int Slot, Currency Currency, int Denomination, int Expected, int Counted, int Difference, decimal DifferenceTry);

public sealed record ReconciliationDto(
    Guid Id,
    Guid AtmId,
    ReconciliationStatus Status,
    string PerformedBy,
    DateTimeOffset CreatedAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    decimal TotalDifferenceTry,
    decimal ApprovalThresholdTry,
    IReadOnlyList<ReconciliationLineDto> Lines);

public sealed record ExchangeRateDto(Currency Currency, decimal RateToTry);

/// <summary>Manual counts and their four-eyes approval (Part A #8, B9).</summary>
public sealed class ReconciliationService(IAtmDbContext db, TimeProvider timeProvider)
{
    public Task<ReconciliationDto> ReconcileAsync(
        Guid atmId, string? user, IReadOnlyList<CassetteCount> counts, CancellationToken cancellationToken) =>
        db.ChangeAtmAsync(
            atmId,
            async atm =>
            {
                var rates = await db.ExchangeRates.ToDictionaryAsync(r => r.Currency, r => r.RateToTry, cancellationToken);
                var reconciliation = Reconciliation.Perform(atm, user!, counts, rates, timeProvider.GetUtcNow());
                db.Reconciliations.Add(reconciliation);
                return reconciliation.ToDto();
            },
            cancellationToken);

    public async Task<ReconciliationDto> ApproveAsync(Guid reconciliationId, string? user, CancellationToken cancellationToken)
    {
        var atmId = await db.Reconciliations
            .Where(r => r.Id == reconciliationId)
            .Select(r => (Guid?)r.AtmId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw NotFound(reconciliationId);

        return await db.ChangeAtmAsync(
            atmId,
            async atm =>
            {
                var reconciliation = await db.Reconciliations.SingleAsync(r => r.Id == reconciliationId, cancellationToken);
                reconciliation.Approve(atm, user!, timeProvider.GetUtcNow());
                return reconciliation.ToDto();
            },
            cancellationToken);
    }

    public async Task<ReconciliationDto> GetAsync(Guid reconciliationId, CancellationToken cancellationToken) =>
        (await db.Reconciliations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == reconciliationId, cancellationToken)
            ?? throw NotFound(reconciliationId)).ToDto();

    public async Task<IReadOnlyList<ExchangeRateDto>> ListExchangeRatesAsync(CancellationToken cancellationToken) =>
        (await db.ExchangeRates.AsNoTracking().ToListAsync(cancellationToken))
            // Sorted in memory: the column holds the enum name, so SQL would sort alphabetically.
            .OrderBy(r => r.Currency)
            .Select(r => new ExchangeRateDto(r.Currency, r.RateToTry))
            .ToList();

    private static DomainException NotFound(Guid id) =>
        DomainException.NotFound(ErrorCodes.ReconciliationNotFound, $"Reconciliation {id} was not found.");
}

internal static class ReconciliationMapping
{
    public static ReconciliationDto ToDto(this Reconciliation r) => new(
        r.Id,
        r.AtmId,
        r.Status,
        r.PerformedBy,
        r.CreatedAt,
        r.ApprovedBy,
        r.ApprovedAt,
        r.TotalDifferenceTry,
        Reconciliation.ApprovalThresholdTry,
        r.Lines
            .OrderBy(l => l.Slot).ThenBy(l => l.Currency).ThenByDescending(l => l.Denomination)
            .Select(l => new ReconciliationLineDto(
                l.Slot, l.Currency, l.Denomination, l.Expected, l.Counted, l.Difference, l.DifferenceTry))
            .ToList());
}
