using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Deposits;
using AtmCash.Domain.Reconciliations;

namespace AtmCash.UnitTests.Reconciliations;

public class ReconciliationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<Currency, decimal> Rates = new()
    {
        [Currency.TRY] = 1m,
        [Currency.USD] = 40m,
        [Currency.EUR] = 45m,
    };

    [Fact]
    public void Small_differences_are_booked_and_completed_without_approval()
    {
        // System: TRY100 x50, TRY50 x20, TRY20 x10, USD100 x5, deposit EUR50 x4.
        var atm = AtmInMaintenance();

        // 2 x TRY 100 missing (-200), 1 x TRY 20 extra (+20): 220 TRY in total.
        var reconciliation = Reconciliation.Perform(atm, "ayse", Counts(48, 20, 11, 5, (Currency.EUR, 50, 4)), Rates, Now);

        Assert.Equal(ReconciliationStatus.Completed, reconciliation.Status);
        Assert.Equal(220m, reconciliation.TotalDifferenceTry);
        Assert.Null(atm.PendingReconciliationId);
        Assert.Equal([48, 20, 11, 5, 4], atm.Cassettes.Select(c => c.Count));

        var adjustments = atm.DequeueNewMovements().Where(m => m.Type == MovementType.ReconciliationAdjustment).ToList();
        Assert.Equal([-2, 1], adjustments.Select(m => m.Quantity));
        Assert.All(adjustments, m => Assert.Equal(reconciliation.Id, m.CorrelationId));

        atm.PutInService();
        Assert.Equal(AtmStatus.InService, atm.Status);
    }

    [Fact]
    public void Differences_are_valued_in_try_using_exchange_rates()
    {
        var atm = AtmInMaintenance();

        // 1 x USD 100 missing = 4,000 TRY at a rate of 40; 2 x EUR 50 extra = 4,500 TRY at 45.
        var reconciliation = Reconciliation.Perform(atm, "ayse", Counts(50, 20, 10, 4, (Currency.EUR, 50, 6)), Rates, Now);

        Assert.Equal(
            [-4_000m, 4_500m],
            reconciliation.Lines.Where(l => l.Difference != 0).Select(l => l.DifferenceTry));
        Assert.Equal(8_500m, reconciliation.TotalDifferenceTry);
    }

    [Fact]
    public void Opposite_differences_do_not_cancel_out()
    {
        var atm = AtmInMaintenance();

        // -6 x TRY 100 and +12 x TRY 50: net zero but 1,200 TRY of differences.
        var reconciliation = Reconciliation.Perform(atm, "ayse", Counts(44, 32, 10, 5, (Currency.EUR, 50, 4)), Rates, Now);

        Assert.Equal(1_200m, reconciliation.TotalDifferenceTry);
        Assert.Equal(ReconciliationStatus.AwaitingApproval, reconciliation.Status);
    }

    [Fact]
    public void Exactly_the_threshold_does_not_need_approval()
    {
        var atm = AtmInMaintenance();

        var reconciliation = Reconciliation.Perform(atm, "ayse", Counts(40, 20, 10, 5, (Currency.EUR, 50, 4)), Rates, Now);

        Assert.Equal(1_000m, reconciliation.TotalDifferenceTry);
        Assert.Equal(ReconciliationStatus.Completed, reconciliation.Status);
    }

    [Fact]
    public void Large_difference_blocks_service_until_a_different_user_approves()
    {
        var atm = AtmInMaintenance();
        var reconciliation = Reconciliation.Perform(atm, "ayse", Counts(30, 20, 10, 5, (Currency.EUR, 50, 4)), Rates, Now);

        Assert.Equal(ReconciliationStatus.AwaitingApproval, reconciliation.Status);
        Assert.Equal(reconciliation.Id, atm.PendingReconciliationId);
        Assert.Equal(30, atm.GetCassette(1).Count);
        Assert.Equal(ErrorCodes.ReconciliationAwaitingApproval, Assert.Throws<DomainException>(atm.PutInService).Code);

        var sameUser = Assert.Throws<DomainException>(() => reconciliation.Approve(atm, " AYSE ", Now));
        Assert.Equal((ErrorKind.Forbidden, ErrorCodes.SameUserApproval), (sameUser.Kind, sameUser.Code));

        reconciliation.Approve(atm, "mehmet", Now.AddMinutes(5));

        Assert.Equal((ReconciliationStatus.Approved, "mehmet"), (reconciliation.Status, reconciliation.ApprovedBy));
        Assert.Null(atm.PendingReconciliationId);
        atm.PutInService();
        Assert.Equal(AtmStatus.InService, atm.Status);
    }

    [Fact]
    public void Approval_is_only_possible_while_awaiting_it()
    {
        var atm = AtmInMaintenance();
        var small = Reconciliation.Perform(atm, "ayse", Counts(50, 20, 10, 5, (Currency.EUR, 50, 4)), Rates, Now);

        var error = Assert.Throws<DomainException>(() => small.Approve(atm, "mehmet", Now));

        Assert.Equal(ErrorCodes.ReconciliationNotAwaitingApproval, error.Code);
    }

    [Fact]
    public void A_new_count_is_refused_while_one_awaits_approval()
    {
        var atm = AtmInMaintenance();
        Reconciliation.Perform(atm, "ayse", Counts(30, 20, 10, 5, (Currency.EUR, 50, 4)), Rates, Now);

        var error = Assert.Throws<DomainException>(() =>
            Reconciliation.Perform(atm, "ayse", Counts(50, 20, 10, 5, (Currency.EUR, 50, 4)), Rates, Now));

        Assert.Equal(ErrorCodes.ReconciliationAwaitingApproval, error.Code);
    }

    [Fact]
    public void Deposit_cassette_count_replaces_its_mixed_holdings()
    {
        var atm = AtmInMaintenance();

        // EUR 50 x4 counted as 3, plus an unexpected USD 20 note.
        var reconciliation = Reconciliation.Perform(
            atm, "ayse", Counts(50, 20, 10, 5, (Currency.EUR, 50, 3), (Currency.USD, 20, 1)), Rates, Now);

        Assert.Equal(
            [(Currency.USD, 20, 0, 1), (Currency.EUR, 50, 4, 3)],
            reconciliation.Lines.Where(l => l.Slot == 5).Select(l => (l.Currency, l.Denomination, l.Expected, l.Counted)));
        Assert.Equal(4, atm.GetCassette(5).Count);
    }

    [Fact]
    public void Reconciliation_is_only_allowed_in_maintenance()
    {
        var atm = AtmInMaintenance();
        atm.PutInService();

        var error = Assert.Throws<DomainException>(() =>
            Reconciliation.Perform(atm, "ayse", Counts(50, 20, 10, 5, (Currency.EUR, 50, 4)), Rates, Now));

        Assert.Equal(ErrorCodes.OperationNotAllowedInState, error.Code);
    }

    [Fact]
    public void Invalid_counts_are_rejected()
    {
        var atm = AtmInMaintenance();
        var full = Counts(50, 20, 10, 5, (Currency.EUR, 50, 4));
        atm.DequeueNewMovements();

        Assert.Equal(ErrorCodes.InvalidReconciliationCount, Reject(atm, full[..4]));
        Assert.Equal(ErrorCodes.InvalidReconciliationCount, Reject(atm, [.. full, full[0]]));
        Assert.Equal(ErrorCodes.InvalidReconciliationCount, Reject(atm, [full[0] with { Count = -1 }, .. full[1..]]));
        Assert.Equal(ErrorCodes.InvalidReconciliationCount, Reject(atm, [full[0] with { Count = 101 }, .. full[1..]]));
        Assert.Equal(ErrorCodes.InvalidReconciliationCount, Reject(atm, [.. full[..4], full[4] with { Notes = null, Count = 4 }]));
        Assert.Equal(ErrorCodes.UserRequired, Assert.Throws<DomainException>(() => Reconciliation.Perform(atm, " ", full, Rates, Now)).Code);
        Assert.Empty(atm.NewMovements);
    }

    [Fact]
    public void Adjustments_keep_the_ledger_in_line_with_balances()
    {
        var atm = AtmInMaintenance();
        var before = atm.DequeueNewMovements();

        Reconciliation.Perform(atm, "ayse", Counts(30, 25, 0, 5, (Currency.USD, 100, 2)), Rates, Now);
        var movements = before.Concat(atm.DequeueNewMovements()).ToList();

        Assert.All(atm.Cassettes, c => Assert.Equal(c.Count, movements.Where(m => m.CassetteId == c.Id).Sum(m => m.Quantity)));
    }

    private static string Reject(Atm atm, CassetteCount[] counts) =>
        Assert.Throws<DomainException>(() => Reconciliation.Perform(atm, "ayse", counts, Rates, Now)).Code;

    private static CassetteCount[] Counts(
        int try100, int try50, int try20, int usd100, params (Currency Currency, int Denomination, int Count)[] deposit) =>
    [
        new(1, try100, null),
        new(2, try50, null),
        new(3, try20, null),
        new(4, usd100, null),
        new(5, null, deposit.Select(d => new CountedNote(d.Currency, d.Denomination, d.Count)).ToList()),
    ];

    private static Atm AtmInMaintenance()
    {
        var atm = Atm.Create("IST-1");
        atm.ConfigureCassettes(
        [
            new RecyclingCassetteSpec(Currency.TRY, 100),
            new RecyclingCassetteSpec(Currency.TRY, 50),
            new RecyclingCassetteSpec(Currency.TRY, 20),
            new RecyclingCassetteSpec(Currency.USD, 100),
        ]);
        atm.LoadCash([new CassetteLoad(1, 50), new CassetteLoad(2, 20), new CassetteLoad(3, 10), new CassetteLoad(4, 5)]);
        atm.PutInService();
        atm.Deposit(Guid.NewGuid(), [new DepositNote(Currency.EUR, 50, 4)], 0);
        atm.EnterMaintenance();
        return atm;
    }
}
