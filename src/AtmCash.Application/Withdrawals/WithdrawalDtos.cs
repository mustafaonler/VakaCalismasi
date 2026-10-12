using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Withdrawals;

namespace AtmCash.Application.Withdrawals;

public sealed record BreakdownDto(int Amount, int NoteCount, IReadOnlyList<NoteLine> Notes);

public sealed record QuoteDto(
    Guid Id,
    Guid AtmId,
    Currency Currency,
    int RequestedAmount,
    QuoteStatus Status,
    DateTimeOffset ExpiresAt,
    BreakdownDto? Exact,
    BreakdownDto? Lower,
    BreakdownDto? Upper,
    QuoteOption? ConfirmedOption,
    int? DispensedAmount);

public sealed record DispensedFromDto(int Slot, int Denomination, int Count);

public sealed record WithdrawalResultDto(
    Guid QuoteId,
    Guid AtmId,
    Currency Currency,
    QuoteOption Option,
    int Amount,
    IReadOnlyList<NoteLine> Notes,
    IReadOnlyList<DispensedFromDto> Cassettes,
    DateTimeOffset ConfirmedAt);

internal static class WithdrawalMapping
{
    public static QuoteDto ToDto(this WithdrawalQuote quote) => new(
        quote.Id,
        quote.AtmId,
        quote.Currency,
        quote.RequestedAmount,
        quote.Status,
        quote.ExpiresAt,
        Breakdown(quote, QuoteOption.Exact),
        Breakdown(quote, QuoteOption.Lower),
        Breakdown(quote, QuoteOption.Upper),
        quote.ConfirmedOption,
        quote.DispensedAmount);

    public static WithdrawalResultDto ToResult(this WithdrawalQuote quote, IReadOnlyList<NoteAllocation> dispensed)
    {
        var option = quote.ConfirmedOption!.Value;
        return new WithdrawalResultDto(
            quote.Id,
            quote.AtmId,
            quote.Currency,
            option,
            quote.DispensedAmount!.Value,
            quote.Offer(option)!.Notes,
            dispensed.Select(d => new DispensedFromDto(d.Slot, d.Denomination, d.Quantity)).ToList(),
            quote.CompletedAt!.Value);
    }

    private static BreakdownDto? Breakdown(WithdrawalQuote quote, QuoteOption option) =>
        quote.Offer(option) is { } offer ? new BreakdownDto(offer.Amount, offer.NoteCount, offer.Notes) : null;
}
