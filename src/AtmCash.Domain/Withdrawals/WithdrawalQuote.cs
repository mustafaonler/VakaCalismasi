using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;

namespace AtmCash.Domain.Withdrawals;

public enum QuoteStatus
{
    Active,
    Confirmed,
    Cancelled,
    Expired,
}

public enum QuoteOption
{
    Exact,
    Lower,
    Upper,
}

/// <summary>One denomination line of an offered breakdown.</summary>
public sealed record QuoteOfferLine(QuoteOption Option, int Denomination, int Count);

/// <summary>Notes reserved in one cassette; <see cref="Order"/> keeps the fill-ratio order used at reservation.</summary>
public sealed record QuoteReservation(int Order, int Slot, int Denomination, int Quantity);

/// <summary>
/// First step of a two-step withdrawal (B4). Creating a quote reserves the notes of every offered
/// breakdown; confirming pays out the chosen one and releases the rest.
/// </summary>
/// <remarks>
/// The lower and upper alternatives draw on the same stock, so the reservation per denomination is
/// the maximum either alternative needs. Each alternative fits the stock on its own and the stock
/// constraint is per denomination, so that maximum always fits as well.
/// </remarks>
public sealed class WithdrawalQuote
{
    public static readonly TimeSpan Validity = TimeSpan.FromSeconds(60);

    private readonly List<QuoteOfferLine> _offerLines = [];
    private readonly List<QuoteReservation> _reservations = [];

    private WithdrawalQuote()
    {
        CardHash = null!;
    }

    private WithdrawalQuote(Guid atmId, string cardHash, Currency currency, int requestedAmount, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        AtmId = atmId;
        CardHash = cardHash;
        Currency = currency;
        RequestedAmount = requestedAmount;
        Status = QuoteStatus.Active;
        CreatedAt = now;
        ExpiresAt = now + Validity;
    }

    public Guid Id { get; private set; }

    public Guid AtmId { get; private set; }

    /// <summary>Keyed hash of the card number; the number itself is never stored (B5).</summary>
    public string CardHash { get; private set; }

    public Currency Currency { get; private set; }

    public int RequestedAmount { get; private set; }

    public QuoteStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public QuoteOption? ConfirmedOption { get; private set; }

    public int? DispensedAmount { get; private set; }

    /// <summary>The largest amount this quote can still turn into; counts towards the daily limit while active.</summary>
    public int HeldAmount { get; private set; }

    public IReadOnlyList<QuoteOfferLine> OfferLines => _offerLines;

    public IReadOnlyList<QuoteReservation> Reservations => _reservations;

    public static WithdrawalQuote Create(
        Atm atm, string cardHash, Currency currency, int requestedAmount, WithdrawalOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(atm);
        ArgumentNullException.ThrowIfNull(options);
        atm.EnsureInService("request a withdrawal");

        var offers = new (QuoteOption Option, NoteBreakdown? Breakdown)[]
        {
            (QuoteOption.Exact, options.Exact),
            (QuoteOption.Lower, options.Lower),
            (QuoteOption.Upper, options.Upper),
        }.Where(o => o.Breakdown is not null).ToList();

        if (offers.Count == 0)
        {
            throw DomainException.Conflict(
                ErrorCodes.NoPayableAmount, $"ATM {atm.Code} cannot pay any {currency} amount near {requestedAmount}.");
        }

        var quote = new WithdrawalQuote(atm.Id, cardHash, currency, requestedAmount, now);
        foreach (var (option, breakdown) in offers)
        {
            quote._offerLines.AddRange(breakdown!.Notes.Select(n => new QuoteOfferLine(option, n.Denomination, n.Count)));
        }

        quote.HeldAmount = offers.Max(o => o.Breakdown!.Amount);

        var reservePerDenomination = quote._offerLines
            .GroupBy(l => l.Denomination)
            .ToDictionary(g => g.Key, g => g.Max(l => l.Count));
        var allocations = atm.ReserveNotes(currency, reservePerDenomination, quote.Id);
        quote._reservations.AddRange(allocations.Select((a, i) => new QuoteReservation(i, a.Slot, a.Denomination, a.Quantity)));

        return quote;
    }

    public NoteBreakdown? Offer(QuoteOption option)
    {
        var lines = _offerLines
            .Where(l => l.Option == option)
            .OrderByDescending(l => l.Denomination)
            .Select(l => new NoteLine(l.Denomination, l.Count))
            .ToList();
        return lines.Count == 0 ? null : new NoteBreakdown(lines.Sum(l => l.Denomination * l.Count), lines);
    }

    public bool IsPastExpiry(DateTimeOffset now) => Status == QuoteStatus.Active && now >= ExpiresAt;

    /// <summary>Pays out the chosen breakdown and releases every other reserved note.</summary>
    /// <param name="option">Which offer to take; may be omitted when the quote has an exact match.</param>
    /// <returns>The notes dispensed, per cassette.</returns>
    public IReadOnlyList<NoteAllocation> Confirm(Atm atm, QuoteOption? option, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(atm);
        EnsureActive("confirmed");
        if (IsPastExpiry(now))
        {
            throw DomainException.Conflict(ErrorCodes.QuoteExpired, $"Quote {Id} expired at {ExpiresAt:O}.");
        }

        var chosen = option
            ?? (Offer(QuoteOption.Exact) is not null
                ? QuoteOption.Exact
                : throw DomainException.Validation(
                    ErrorCodes.QuoteOptionRequired, "The quote has no exact match; choose the LOWER or UPPER alternative."));
        var offer = Offer(chosen)
            ?? throw DomainException.Validation(ErrorCodes.QuoteOptionNotOffered, $"Quote {Id} has no {chosen} offer.");

        var toDispense = new List<NoteAllocation>();
        var toRelease = new List<NoteAllocation>();
        var stillNeeded = offer.Notes.ToDictionary(n => n.Denomination, n => n.Count);
        foreach (var reservation in _reservations.OrderBy(r => r.Order))
        {
            var take = Math.Min(stillNeeded.GetValueOrDefault(reservation.Denomination), reservation.Quantity);
            stillNeeded[reservation.Denomination] = stillNeeded.GetValueOrDefault(reservation.Denomination) - take;
            toDispense.Add(new NoteAllocation(reservation.Slot, reservation.Denomination, take));
            toRelease.Add(new NoteAllocation(reservation.Slot, reservation.Denomination, reservation.Quantity - take));
        }

        atm.Dispense(toDispense, Id);
        atm.ReleaseReservations(toRelease, Id);

        Status = QuoteStatus.Confirmed;
        ConfirmedOption = chosen;
        DispensedAmount = offer.Amount;
        CompletedAt = now;
        return toDispense.Where(a => a.Quantity > 0).ToList();
    }

    public void Cancel(Atm atm, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(atm);
        EnsureActive("cancelled");
        ReleaseAll(atm);
        Status = QuoteStatus.Cancelled;
        CompletedAt = now;
    }

    /// <summary>Releases the reservation of a quote whose 60 seconds have passed.</summary>
    public void Expire(Atm atm, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(atm);
        if (Status != QuoteStatus.Active)
        {
            return;
        }

        ReleaseAll(atm);
        Status = QuoteStatus.Expired;
        CompletedAt = now;
    }

    private void ReleaseAll(Atm atm) =>
        atm.ReleaseReservations(_reservations.Select(r => new NoteAllocation(r.Slot, r.Denomination, r.Quantity)), Id);

    private void EnsureActive(string action)
    {
        var code = Status switch
        {
            QuoteStatus.Active => null,
            QuoteStatus.Confirmed => ErrorCodes.QuoteAlreadyConfirmed,
            QuoteStatus.Cancelled => ErrorCodes.QuoteCancelled,
            _ => ErrorCodes.QuoteExpired,
        };
        if (code is not null)
        {
            throw DomainException.Conflict(code, $"Quote {Id} is {Status.ToString().ToUpperInvariant()} and cannot be {action}.");
        }
    }
}
