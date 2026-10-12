using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Deposits;
using AtmCash.Domain.Reconciliations;
using AtmCash.Domain.Withdrawals;

namespace AtmCash.UnitTests.Atms;

public class LowCashAlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Quote_that_takes_available_notes_below_ten_raises_one_alert()
    {
        var atm = InServiceAtm(try100: 12);

        var quote = Quote(atm, 300);

        var alert = Assert.Single(atm.DequeueNewAlerts());
        Assert.Equal(
            new LowCashAlert(atm.Id, atm.Code, 1, Currency.TRY, 100, 9, Cassette.LowCashThreshold, MovementType.Reservation, quote.Id),
            alert);
    }

    [Fact]
    public void No_alert_while_staying_at_or_above_the_threshold()
    {
        var atm = InServiceAtm(try100: 12);

        Quote(atm, 200);

        Assert.Equal(10, atm.GetCassette(1).Available);
        Assert.Empty(atm.NewAlerts);
    }

    [Fact]
    public void Further_drops_below_the_threshold_do_not_repeat_the_alert()
    {
        var atm = InServiceAtm(try100: 12);
        Quote(atm, 300);
        atm.DequeueNewAlerts();

        Quote(atm, 200);

        Assert.Empty(atm.NewAlerts);
    }

    [Fact]
    public void Confirming_a_withdrawal_does_not_raise_a_second_alert()
    {
        // Confirm releases and removes the same notes: available does not change.
        var atm = InServiceAtm(try100: 12);
        var quote = Quote(atm, 300);
        atm.DequeueNewAlerts();

        quote.Confirm(atm, null, Now);

        Assert.Equal(9, atm.GetCassette(1).Available);
        Assert.Empty(atm.NewAlerts);
    }

    [Fact]
    public void Reconciliation_shortage_below_the_threshold_raises_an_alert()
    {
        var atm = InServiceAtm(try100: 50, usd100: 15);
        atm.EnterMaintenance();

        var reconciliation = Reconciliation.Perform(
            atm,
            "ayse",
            [new(1, 50, null), new(2, 0, null), new(3, 0, null), new(4, 7, null), new(5, null, [])],
            new Dictionary<Currency, decimal> { [Currency.TRY] = 1m, [Currency.USD] = 40m, [Currency.EUR] = 45m },
            Now);

        var alert = Assert.Single(atm.DequeueNewAlerts());
        Assert.Equal(
            (4, Currency.USD, 7, MovementType.ReconciliationAdjustment, (Guid?)reconciliation.Id),
            (alert.Slot, alert.Currency, alert.AvailableCount, alert.Trigger, alert.CorrelationId));
    }

    [Fact]
    public void Deposit_cassette_never_raises_an_alert()
    {
        var atm = InServiceAtm(try100: 50);
        atm.Deposit(Guid.NewGuid(), [new DepositNote(Currency.EUR, 50, 20)], 0);
        atm.EnterMaintenance();

        atm.UnloadCassettes([5]);

        Assert.Empty(atm.NewAlerts);
    }

    [Fact]
    public void Expired_quote_released_and_reserved_again_alerts_again()
    {
        var atm = InServiceAtm(try100: 12);
        Quote(atm, 300).Expire(atm, Now.AddMinutes(2));
        atm.DequeueNewAlerts();

        Quote(atm, 300);

        Assert.Single(atm.NewAlerts);
    }

    private static WithdrawalQuote Quote(Atm atm, int amount) =>
        WithdrawalQuote.Create(
            atm, "card-hash", Currency.TRY, amount, NoteSelector.Select(atm.AvailableNotes(Currency.TRY), amount), Now);

    private static Atm InServiceAtm(int try100, int usd100 = 20)
    {
        var atm = Atm.Create("IST-1");
        atm.ConfigureCassettes(
        [
            new RecyclingCassetteSpec(Currency.TRY, 100),
            new RecyclingCassetteSpec(Currency.TRY, 50),
            new RecyclingCassetteSpec(Currency.TRY, 20),
            new RecyclingCassetteSpec(Currency.USD, 100),
        ]);
        atm.LoadCash([new CassetteLoad(1, try100), new CassetteLoad(4, usd100)]);
        atm.PutInService();
        return atm;
    }
}
