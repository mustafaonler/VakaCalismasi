using System.Text.Json;
using AtmCash.Application.Abstractions;
using AtmCash.Application.Atms;
using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Withdrawals;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Application.Withdrawals;

/// <summary>Two-step withdrawal: quote, then confirm or cancel (B3-B6).</summary>
public sealed class WithdrawalService(IAtmDbContext db, ICardHasher cardHasher, TimeProvider timeProvider)
{
    public async Task<QuoteDto> RequestQuoteAsync(
        Guid atmId, string cardNumber, Currency currency, int amount, CancellationToken cancellationToken)
    {
        if (amount <= 0)
        {
            throw DomainException.Validation(ErrorCodes.InvalidAmount, "Amount must be greater than zero.");
        }

        var cardHash = cardHasher.Hash(cardNumber);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Lock order is always card, then ATM; confirm and expiry take only the ATM lock, so no cycle exists.
        await db.LockCardAsync(cardHash, cancellationToken);
        await db.LockAtmAsync(atmId, cancellationToken);
        var atm = await db.LoadAtmAsync(atmId, tracking: true, cancellationToken);
        atm.EnsureInService("request a withdrawal");
        await ExpireOverdueAsync(atm, now, cancellationToken);

        var remaining = DailyLimits.For(currency) - await UsedTodayAsync(cardHash, currency, now, cancellationToken);
        if (amount > remaining)
        {
            throw DomainException.Conflict(
                ErrorCodes.DailyLimitExceeded,
                $"Requested {amount} {currency} exceeds the remaining daily limit of {remaining} {currency} for this card.");
        }

        var options = NoteSelector.Select(atm.AvailableNotes(currency), amount, upperAmountCap: remaining);
        var quote = WithdrawalQuote.Create(atm, cardHash, currency, amount, options, now);
        db.Quotes.Add(quote);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return quote.ToDto();
    }

    public async Task<QuoteDto> GetQuoteAsync(Guid quoteId, CancellationToken cancellationToken) =>
        (await db.Quotes.AsNoTracking().SingleOrDefaultAsync(q => q.Id == quoteId, cancellationToken)
            ?? throw QuoteNotFound(quoteId)).ToDto();

    /// <summary>
    /// Confirms a quote. The idempotency record is written in the same transaction as the cash
    /// movements and is checked while holding the ATM lock, so a retried or concurrent request with
    /// the same key either finds the stored result or waits for the first one to commit.
    /// </summary>
    public async Task<WithdrawalResultDto> ConfirmAsync(
        Guid quoteId, QuoteOption? option, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
        {
            throw DomainException.Validation(
                ErrorCodes.IdempotencyKeyRequired, "An Idempotency-Key header of at most 128 characters is required.");
        }

        var atmId = await AtmOfQuoteAsync(quoteId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.LockAtmAsync(atmId, cancellationToken);

        var previous = await db.IdempotencyRecords.SingleOrDefaultAsync(r => r.Key == idempotencyKey, cancellationToken);
        if (previous is not null)
        {
            if (previous.QuoteId != quoteId)
            {
                throw DomainException.Conflict(
                    ErrorCodes.IdempotencyKeyReused, "This Idempotency-Key was already used for a different quote.");
            }

            return JsonSerializer.Deserialize<WithdrawalResultDto>(previous.Response)!;
        }

        var quote = await db.Quotes.SingleAsync(q => q.Id == quoteId, cancellationToken);
        var atm = await db.LoadAtmAsync(atmId, tracking: true, cancellationToken);

        if (quote.IsPastExpiry(now))
        {
            // Release the reservation right away instead of waiting for the background sweep.
            quote.Expire(atm, now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw DomainException.Conflict(ErrorCodes.QuoteExpired, $"Quote {quoteId} expired at {quote.ExpiresAt:O}.");
        }

        var dispensed = quote.Confirm(atm, option, now);
        var result = quote.ToResult(dispensed);
        db.IdempotencyRecords.Add(new IdempotencyRecord(idempotencyKey, quoteId, JsonSerializer.Serialize(result), now));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<QuoteDto> CancelAsync(Guid quoteId, CancellationToken cancellationToken)
    {
        var atmId = await AtmOfQuoteAsync(quoteId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.LockAtmAsync(atmId, cancellationToken);
        var quote = await db.Quotes.SingleAsync(q => q.Id == quoteId, cancellationToken);
        var atm = await db.LoadAtmAsync(atmId, tracking: true, cancellationToken);

        quote.Cancel(atm, timeProvider.GetUtcNow());

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return quote.ToDto();
    }

    /// <summary>Releases the reservations of every quote whose validity has passed. Returns the number expired.</summary>
    public async Task<int> ExpireOverdueQuotesAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var atmIds = await db.Quotes
            .Where(q => q.Status == QuoteStatus.Active && q.ExpiresAt <= now)
            .Select(q => q.AtmId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var expired = 0;
        foreach (var atmId in atmIds)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.LockAtmAsync(atmId, cancellationToken);
            var atm = await db.LoadAtmAsync(atmId, tracking: true, cancellationToken);
            expired += await ExpireOverdueAsync(atm, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return expired;
    }

    /// <summary>Caller must hold the ATM lock.</summary>
    private async Task<int> ExpireOverdueAsync(Atm atm, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var overdue = await db.Quotes
            .Where(q => q.AtmId == atm.Id && q.Status == QuoteStatus.Active && q.ExpiresAt <= now)
            .ToListAsync(cancellationToken);
        overdue.ForEach(q => q.Expire(atm, now));
        return overdue.Count;
    }

    /// <summary>
    /// Confirmed withdrawals of the current UTC day plus amounts held by quotes that are still open:
    /// reserved but unconfirmed amounts count towards the limit (B5).
    /// </summary>
    private async Task<int> UsedTodayAsync(string cardHash, Currency currency, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var cardQuotes = db.Quotes.Where(q => q.CardHash == cardHash && q.Currency == currency);

        var confirmed = await cardQuotes
            .Where(q => q.Status == QuoteStatus.Confirmed && q.CompletedAt >= dayStart)
            .SumAsync(q => q.DispensedAmount ?? 0, cancellationToken);
        var held = await cardQuotes
            .Where(q => q.Status == QuoteStatus.Active && q.ExpiresAt > now)
            .SumAsync(q => q.HeldAmount, cancellationToken);
        return confirmed + held;
    }

    private async Task<Guid> AtmOfQuoteAsync(Guid quoteId, CancellationToken cancellationToken) =>
        await db.Quotes.Where(q => q.Id == quoteId).Select(q => (Guid?)q.AtmId).SingleOrDefaultAsync(cancellationToken)
        ?? throw QuoteNotFound(quoteId);

    private static DomainException QuoteNotFound(Guid quoteId) =>
        DomainException.NotFound(ErrorCodes.QuoteNotFound, $"Quote {quoteId} was not found.");
}
