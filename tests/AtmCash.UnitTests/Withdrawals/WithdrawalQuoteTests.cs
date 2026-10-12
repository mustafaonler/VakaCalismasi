using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Withdrawals;

namespace AtmCash.UnitTests.Withdrawals;

public class WithdrawalQuoteTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Exact_quote_reserves_its_notes_and_confirm_pays_them_out()
    {
        var atm = Atm([(Currency.TRY, 100, 5), (Currency.TRY, 50, 5), (Currency.TRY, 20, 5), (Currency.USD, 100, 5)]);

        var quote = Quote(atm, 170);

        Assert.Equal(170, quote.HeldAmount);
        Assert.Equal([1, 1, 1], [atm.GetCassette(1).Reserved, atm.GetCassette(2).Reserved, atm.GetCassette(3).Reserved]);

        var dispensed = quote.Confirm(atm, null, Now.AddSeconds(30));

        Assert.Equal(QuoteStatus.Confirmed, quote.Status);
        Assert.Equal(170, quote.DispensedAmount);
        Assert.Equal(3, dispensed.Count);
        Assert.Equal([4, 4, 4], [atm.GetCassette(1).Count, atm.GetCassette(2).Count, atm.GetCassette(3).Count]);
        Assert.All(atm.Cassettes, c => Assert.Equal(0, c.Reserved));
    }

    [Fact]
    public void Alternatives_reserve_enough_for_either_and_confirm_releases_the_rest()
    {
        // Acceptance case 2: 50x1, 20x2, request 80 -> lower 70 (50+20), upper 90 (50+20x2).
        var atm = Atm([(Currency.TRY, 100, 0), (Currency.TRY, 50, 1), (Currency.TRY, 20, 2), (Currency.USD, 100, 5)]);

        var quote = Quote(atm, 80);

        Assert.Null(quote.Offer(QuoteOption.Exact));
        Assert.Equal(70, quote.Offer(QuoteOption.Lower)!.Amount);
        Assert.Equal(90, quote.Offer(QuoteOption.Upper)!.Amount);
        Assert.Equal(90, quote.HeldAmount);
        Assert.Equal((1, 2), (atm.GetCassette(2).Reserved, atm.GetCassette(3).Reserved));

        quote.Confirm(atm, QuoteOption.Lower, Now);

        Assert.Equal((0, 1), (atm.GetCassette(2).Count, atm.GetCassette(3).Count));
        Assert.All(atm.Cassettes, c => Assert.Equal(0, c.Reserved));
    }

    [Fact]
    public void Reserved_notes_cannot_be_offered_again()
    {
        var atm = Atm([(Currency.TRY, 100, 2), (Currency.TRY, 50, 0), (Currency.TRY, 20, 0), (Currency.USD, 100, 5)]);
        Quote(atm, 200);

        var error = Assert.Throws<DomainException>(() => Quote(atm, 100));

        Assert.Equal(ErrorCodes.NoPayableAmount, error.Code);
    }

    [Fact]
    public void Same_denomination_in_two_cassettes_draws_from_the_fuller_one_first()
    {
        var atm = Atm([(Currency.TRY, 100, 30), (Currency.TRY, 100, 60), (Currency.TRY, 20, 0), (Currency.USD, 100, 5)]);

        var quote = Quote(atm, 7_000);

        Assert.Equal((10, 60), (atm.GetCassette(1).Reserved, atm.GetCassette(2).Reserved));
        Assert.Equal([2, 1], quote.Reservations.OrderBy(r => r.Order).Select(r => r.Slot));
    }

    [Fact]
    public void Cancel_and_expiry_release_the_reservation()
    {
        var atm = Atm([(Currency.TRY, 100, 5), (Currency.TRY, 50, 5), (Currency.TRY, 20, 5), (Currency.USD, 100, 5)]);
        var cancelled = Quote(atm, 100);
        var expired = Quote(atm, 50);

        cancelled.Cancel(atm, Now);
        expired.Expire(atm, Now.AddMinutes(2));

        Assert.Equal(QuoteStatus.Cancelled, cancelled.Status);
        Assert.Equal(QuoteStatus.Expired, expired.Status);
        Assert.All(atm.Cassettes, c => Assert.Equal(0, c.Reserved));
        Assert.Equal(5, atm.GetCassette(1).Count);
    }

    [Fact]
    public void Expired_or_cancelled_quote_cannot_be_confirmed()
    {
        var atm = Atm([(Currency.TRY, 100, 5), (Currency.TRY, 50, 5), (Currency.TRY, 20, 5), (Currency.USD, 100, 5)]);
        var late = Quote(atm, 100);
        var cancelled = Quote(atm, 100);
        cancelled.Cancel(atm, Now);

        Assert.Equal(ErrorCodes.QuoteExpired, Assert.Throws<DomainException>(() => late.Confirm(atm, null, Now.AddSeconds(60))).Code);
        Assert.Equal(ErrorCodes.QuoteCancelled, Assert.Throws<DomainException>(() => cancelled.Confirm(atm, null, Now)).Code);
    }

    [Fact]
    public void Quote_without_exact_match_requires_an_offered_option()
    {
        var atm = Atm([(Currency.TRY, 100, 2), (Currency.TRY, 50, 0), (Currency.TRY, 20, 0), (Currency.USD, 100, 5)]);
        var quote = Quote(atm, 250);

        Assert.Equal(ErrorCodes.QuoteOptionRequired, Assert.Throws<DomainException>(() => quote.Confirm(atm, null, Now)).Code);
        Assert.Equal(
            ErrorCodes.QuoteOptionNotOffered,
            Assert.Throws<DomainException>(() => quote.Confirm(atm, QuoteOption.Upper, Now)).Code);
    }

    [Fact]
    public void Withdrawal_movements_reproduce_the_balance()
    {
        var atm = Atm([(Currency.TRY, 100, 5), (Currency.TRY, 50, 5), (Currency.TRY, 20, 5), (Currency.USD, 100, 5)]);
        var quote = Quote(atm, 270);
        quote.Confirm(atm, null, Now);

        var movements = atm.DequeueNewMovements().Where(m => m.CassetteId == atm.GetCassette(1).Id).ToList();

        Assert.Equal(atm.GetCassette(1).Count, movements.Where(m => !m.Type.AffectsReserved()).Sum(m => m.Quantity));
        Assert.Equal(0, movements.Where(m => m.Type.AffectsReserved()).Sum(m => m.Quantity));
        Assert.Contains(movements, m => m.Type == MovementType.Withdrawal && m.CorrelationId == quote.Id);
    }

    private static WithdrawalQuote Quote(Atm atm, int amount) =>
        WithdrawalQuote.Create(
            atm, "card-hash", Currency.TRY, amount, NoteSelector.Select(atm.AvailableNotes(Currency.TRY), amount), Now);

    private static Atm Atm((Currency Currency, int Denomination, int Count)[] cassettes)
    {
        var atm = Domain.Atms.Atm.Create("IST-1");
        atm.ConfigureCassettes(cassettes.Select(c => new RecyclingCassetteSpec(c.Currency, c.Denomination)).ToList());
        atm.LoadCash(cassettes
            .Select((c, i) => new CassetteLoad(i + 1, c.Count))
            .Where(l => l.Quantity > 0)
            .ToList());
        atm.PutInService();
        return atm;
    }
}
