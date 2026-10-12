using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;
using AtmCash.Domain.Deposits;

namespace AtmCash.UnitTests.Deposits;

public class DepositTests
{
    private static readonly Guid DepositId = Guid.NewGuid();

    [Fact]
    public void Notes_with_a_matching_recycling_cassette_go_there_others_go_to_the_deposit_cassette()
    {
        // Slots: 1 TRY 100, 2 TRY 50, 3 TRY 20, 4 USD 100, 5 deposit.
        var atm = InServiceAtm(10, 10, 10, 10);

        var receipt = atm.Deposit(DepositId, [Note(Currency.TRY, 100, 3), Note(Currency.TRY, 500, 2), Note(Currency.EUR, 50, 4)], 0);

        Assert.Equal(
            [
                new DepositPlacement(1, CassetteKind.Recycling, Currency.TRY, 100, 3),
                new DepositPlacement(5, CassetteKind.Deposit, Currency.TRY, 500, 2),
                new DepositPlacement(5, CassetteKind.Deposit, Currency.EUR, 50, 4),
            ],
            receipt.Placements);
        Assert.Empty(receipt.Returned);
        Assert.Equal(13, atm.GetCassette(1).Count);
        Assert.Equal(6, atm.GetCassette(5).Count);
        Assert.Equal(2, atm.GetCassette(5).Holdings.Count);
    }

    [Fact]
    public void Overflow_of_a_full_recycling_cassette_goes_to_the_deposit_cassette()
    {
        var atm = InServiceAtm(95, 10, 10, 10);

        var receipt = atm.Deposit(DepositId, [Note(Currency.TRY, 100, 8)], 0);

        Assert.Equal(
            [
                new DepositPlacement(1, CassetteKind.Recycling, Currency.TRY, 100, 5),
                new DepositPlacement(5, CassetteKind.Deposit, Currency.TRY, 100, 3),
            ],
            receipt.Placements);
        Assert.Empty(receipt.Returned);
        Assert.Equal(Cassette.Capacity, atm.GetCassette(1).Count);
    }

    [Fact]
    public void Notes_that_fit_nowhere_are_returned()
    {
        var atm = InServiceAtm(100, 10, 10, 10);
        atm.Deposit(Guid.NewGuid(), [Note(Currency.EUR, 20, 97)], 0);

        var receipt = atm.Deposit(DepositId, [Note(Currency.TRY, 100, 5), Note(Currency.EUR, 50, 2)], 0);

        Assert.Equal([new DepositPlacement(5, CassetteKind.Deposit, Currency.TRY, 100, 3)], receipt.Placements);
        Assert.Equal(
            [
                new ReturnedNotes(ReturnReason.CassetteFull, Currency.TRY, 100, 2),
                new ReturnedNotes(ReturnReason.CassetteFull, Currency.EUR, 50, 2),
            ],
            receipt.Returned);
        Assert.Equal(Cassette.Capacity, atm.GetCassette(5).Count);
    }

    [Fact]
    public void Unrecognised_notes_are_returned_and_never_reach_a_cassette()
    {
        var atm = InServiceAtm(10, 10, 10, 10);
        atm.DequeueNewMovements();

        var receipt = atm.Deposit(DepositId, [Note(Currency.TRY, 50, 2)], unrecognisedCount: 3);

        Assert.Equal([new ReturnedNotes(ReturnReason.Unrecognised, null, null, 3)], receipt.Returned);
        Assert.Equal(12, atm.GetCassette(2).Count);
        var movement = Assert.Single(atm.DequeueNewMovements());
        Assert.Equal((MovementType.Deposit, 2, DepositId), (movement.Type, movement.Quantity, movement.CorrelationId));
    }

    [Fact]
    public void Only_unrecognised_notes_is_a_valid_deposit_that_returns_everything()
    {
        var atm = InServiceAtm(10, 10, 10, 10);

        var receipt = atm.Deposit(DepositId, [], unrecognisedCount: 4);

        Assert.Empty(receipt.Placements);
        Assert.Equal(4, Assert.Single(receipt.Returned).Count);
    }

    [Fact]
    public void Emptier_of_two_matching_recycling_cassettes_is_filled_first()
    {
        var atm = Atm.Create("IST-1");
        atm.ConfigureCassettes(
        [
            new RecyclingCassetteSpec(Currency.TRY, 100),
            new RecyclingCassetteSpec(Currency.TRY, 100),
            new RecyclingCassetteSpec(Currency.TRY, 50),
            new RecyclingCassetteSpec(Currency.USD, 100),
        ]);
        atm.LoadCash([new CassetteLoad(1, 90), new CassetteLoad(2, 60)]);
        atm.PutInService();

        var receipt = atm.Deposit(DepositId, [Note(Currency.TRY, 100, 50)], 0);

        Assert.Equal(
            [
                new DepositPlacement(2, CassetteKind.Recycling, Currency.TRY, 100, 40),
                new DepositPlacement(1, CassetteKind.Recycling, Currency.TRY, 100, 10),
            ],
            receipt.Placements);
    }

    [Fact]
    public void Repeated_lines_for_the_same_note_are_merged()
    {
        var atm = InServiceAtm(10, 10, 10, 10);

        var receipt = atm.Deposit(DepositId, [Note(Currency.TRY, 20, 2), Note(Currency.TRY, 20, 3)], 0);

        Assert.Equal([new DepositPlacement(3, CassetteKind.Recycling, Currency.TRY, 20, 5)], receipt.Placements);
    }

    [Fact]
    public void Deposit_movements_reproduce_cassette_balances()
    {
        var atm = InServiceAtm(98, 0, 0, 0);
        var loadedBefore = atm.DequeueNewMovements();

        atm.Deposit(DepositId, [Note(Currency.TRY, 100, 5), Note(Currency.USD, 20, 7)], 0);
        var movements = loadedBefore.Concat(atm.DequeueNewMovements()).ToList();

        Assert.All(atm.Cassettes, cassette =>
            Assert.Equal(cassette.Count, movements.Where(m => m.CassetteId == cassette.Id).Sum(m => m.Quantity)));
        Assert.All(
            movements.Where(m => m.Type == MovementType.Deposit),
            m => Assert.Equal(DepositId, m.CorrelationId));
    }

    [Theory]
    [InlineData(AtmStatus.Configured)]
    [InlineData(AtmStatus.Maintenance)]
    public void Deposit_is_only_accepted_in_service(AtmStatus status)
    {
        var atm = Atm.Create("IST-1");
        atm.ConfigureCassettes(StandardConfig);
        if (status == AtmStatus.Maintenance)
        {
            atm.LoadCash([new CassetteLoad(1, 1)]);
            atm.PutInService();
            atm.EnterMaintenance();
        }

        var error = Assert.Throws<DomainException>(() => atm.Deposit(DepositId, [Note(Currency.TRY, 100, 1)], 0));

        Assert.Equal(ErrorCodes.OperationNotAllowedInState, error.Code);
    }

    [Fact]
    public void Invalid_deposits_are_rejected()
    {
        var atm = InServiceAtm(10, 10, 10, 10);

        Assert.Equal(ErrorCodes.InvalidDenomination, Reject(atm, [Note(Currency.TRY, 200, 1)], 0));
        Assert.Equal(ErrorCodes.InvalidQuantity, Reject(atm, [Note(Currency.TRY, 100, 0)], 0));
        Assert.Equal(ErrorCodes.InvalidQuantity, Reject(atm, [Note(Currency.TRY, 100, 1)], -1));
        Assert.Equal(ErrorCodes.EmptyDeposit, Reject(atm, [], 0));
        Assert.DoesNotContain(atm.NewMovements, m => m.Type == MovementType.Deposit);
    }

    private static readonly RecyclingCassetteSpec[] StandardConfig =
    [
        new(Currency.TRY, 100),
        new(Currency.TRY, 50),
        new(Currency.TRY, 20),
        new(Currency.USD, 100),
    ];

    private static string Reject(Atm atm, DepositNote[] notes, int unrecognised) =>
        Assert.Throws<DomainException>(() => atm.Deposit(DepositId, notes, unrecognised)).Code;

    private static DepositNote Note(Currency currency, int denomination, int count) => new(currency, denomination, count);

    private static Atm InServiceAtm(int try100, int try50, int try20, int usd100)
    {
        var atm = Atm.Create("IST-1");
        atm.ConfigureCassettes(StandardConfig);
        atm.LoadCash(new[] { try100, try50, try20, usd100 }
            .Select((count, i) => new CassetteLoad(i + 1, count))
            .Where(l => l.Quantity > 0)
            .ToList());
        atm.PutInService();
        return atm;
    }
}
