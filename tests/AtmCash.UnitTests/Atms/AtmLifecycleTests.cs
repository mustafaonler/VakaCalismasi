using AtmCash.Domain.Atms;
using AtmCash.Domain.Cash;
using AtmCash.Domain.Common;

namespace AtmCash.UnitTests.Atms;

public class AtmLifecycleTests
{
    internal static readonly RecyclingCassetteSpec[] StandardConfig =
    [
        new(Currency.TRY, 100),
        new(Currency.TRY, 50),
        new(Currency.TRY, 20),
        new(Currency.USD, 100),
    ];

    [Fact]
    public void New_atm_is_unconfigured()
    {
        Assert.Equal(AtmStatus.Unconfigured, Atm.Create("IST-1").Status);
    }

    [Fact]
    public void Happy_path_reaches_in_service_and_maintenance()
    {
        var atm = Atm.Create("IST-1");

        atm.ConfigureCassettes(StandardConfig);
        Assert.Equal(AtmStatus.Configured, atm.Status);

        atm.LoadCash([new CassetteLoad(1, 50)]);
        Assert.Equal(AtmStatus.Loaded, atm.Status);

        atm.PutInService();
        Assert.Equal(AtmStatus.InService, atm.Status);

        atm.EnterMaintenance();
        Assert.Equal(AtmStatus.Maintenance, atm.Status);

        atm.PutInService();
        Assert.Equal(AtmStatus.InService, atm.Status);
    }

    [Fact]
    public void Configuration_creates_four_recycling_cassettes_and_one_deposit_cassette()
    {
        var atm = Atm.Create("IST-1");

        atm.ConfigureCassettes(StandardConfig);

        Assert.Equal([1, 2, 3, 4, 5], atm.Cassettes.Select(c => c.Slot));
        Assert.All(atm.Cassettes.Take(4), c => Assert.Equal(CassetteKind.Recycling, c.Kind));
        Assert.Equal(CassetteKind.Deposit, atm.GetCassette(5).Kind);
        Assert.Equal((Currency.USD, 100), (atm.GetCassette(4).Currency!.Value, atm.GetCassette(4).Denomination!.Value));
    }

    [Fact]
    public void Cannot_go_in_service_without_loading_cash()
    {
        var atm = Atm.Create("IST-1");
        atm.ConfigureCassettes(StandardConfig);

        var error = Assert.Throws<DomainException>(atm.PutInService);

        Assert.Equal(ErrorCodes.InvalidStateTransition, error.Code);
        Assert.Equal(ErrorKind.Conflict, error.Kind);
        Assert.Contains("CONFIGURED", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cannot_enter_maintenance_unless_in_service()
    {
        var error = Assert.Throws<DomainException>(Atm.Create("IST-1").EnterMaintenance);

        Assert.Equal(ErrorCodes.InvalidStateTransition, error.Code);
    }

    [Fact]
    public void Cannot_configure_or_load_while_in_service()
    {
        var atm = InService();

        Assert.Equal(
            ErrorCodes.OperationNotAllowedInState,
            Assert.Throws<DomainException>(() => atm.ConfigureCassettes(StandardConfig)).Code);
        Assert.Equal(
            ErrorCodes.OperationNotAllowedInState,
            Assert.Throws<DomainException>(() => atm.LoadCash([new CassetteLoad(1, 1)])).Code);
    }

    [Fact]
    public void Cannot_configure_after_loading_during_initial_setup()
    {
        var atm = Atm.Create("IST-1");
        atm.ConfigureCassettes(StandardConfig);
        atm.LoadCash([new CassetteLoad(1, 10)]);

        var error = Assert.Throws<DomainException>(() => atm.ConfigureCassettes(StandardConfig));

        Assert.Equal(ErrorCodes.OperationNotAllowedInState, error.Code);
    }

    [Theory]
    [InlineData(Currency.TRY, Currency.TRY, Currency.TRY, Currency.TRY)]
    [InlineData(Currency.USD, Currency.EUR, Currency.USD, Currency.TRY)]
    public void Requires_one_or_two_foreign_cassettes(Currency c1, Currency c2, Currency c3, Currency c4)
    {
        var atm = Atm.Create("IST-1");

        var error = Assert.Throws<DomainException>(() => atm.ConfigureCassettes(
            [new(c1, 100), new(c2, 100), new(c3, 50), new(c4, 20)]));

        Assert.Equal(ErrorCodes.ForeignCassetteCount, error.Code);
    }

    [Fact]
    public void Allows_two_foreign_cassettes_and_duplicate_denominations()
    {
        var atm = Atm.Create("IST-1");

        atm.ConfigureCassettes([new(Currency.TRY, 100), new(Currency.TRY, 100), new(Currency.USD, 100), new(Currency.EUR, 50)]);

        Assert.Equal(AtmStatus.Configured, atm.Status);
    }

    [Fact]
    public void Rejects_wrong_cassette_count_and_unsupported_denomination()
    {
        var atm = Atm.Create("IST-1");

        Assert.Equal(
            ErrorCodes.InvalidCassetteConfiguration,
            Assert.Throws<DomainException>(() => atm.ConfigureCassettes(StandardConfig[..3])).Code);
        Assert.Equal(
            ErrorCodes.InvalidDenomination,
            Assert.Throws<DomainException>(() => atm.ConfigureCassettes(
                [new(Currency.TRY, 200), new(Currency.TRY, 50), new(Currency.TRY, 20), new(Currency.USD, 100)])).Code);
    }

    [Fact]
    public void Reconfiguring_a_non_empty_cassette_is_rejected()
    {
        var atm = InService();
        atm.EnterMaintenance();

        var error = Assert.Throws<DomainException>(() => atm.ConfigureCassettes(
            [new(Currency.TRY, 500), .. StandardConfig[1..]]));

        Assert.Equal(ErrorCodes.CassetteNotEmpty, error.Code);
    }

    [Fact]
    public void Reconfiguring_after_unload_is_allowed()
    {
        var atm = InService();
        atm.EnterMaintenance();
        atm.UnloadCassettes([1]);

        atm.ConfigureCassettes([new(Currency.TRY, 500), .. StandardConfig[1..]]);

        Assert.Equal(500, atm.GetCassette(1).Denomination);
        Assert.True(atm.GetCassette(1).IsEmpty);
    }

    [Fact]
    public void Loading_respects_capacity_and_rejects_the_deposit_cassette()
    {
        var atm = Atm.Create("IST-1");
        atm.ConfigureCassettes(StandardConfig);
        atm.LoadCash([new CassetteLoad(1, 90)]);

        Assert.Equal(
            ErrorCodes.CassetteCapacityExceeded,
            Assert.Throws<DomainException>(() => atm.LoadCash([new CassetteLoad(1, 11)])).Code);
        Assert.Equal(
            ErrorCodes.DepositCassetteNotLoadable,
            Assert.Throws<DomainException>(() => atm.LoadCash([new CassetteLoad(5, 1)])).Code);
        Assert.Equal(
            ErrorCodes.CassetteNotFound,
            Assert.Throws<DomainException>(() => atm.LoadCash([new CassetteLoad(6, 1)])).Code);
        Assert.Equal(
            ErrorCodes.InvalidQuantity,
            Assert.Throws<DomainException>(() => atm.LoadCash([new CassetteLoad(2, 0)])).Code);
    }

    [Fact]
    public void Every_cash_change_is_recorded_as_a_movement()
    {
        var atm = Atm.Create("IST-1");
        atm.ConfigureCassettes(StandardConfig);

        atm.LoadCash([new CassetteLoad(1, 40), new CassetteLoad(4, 10)]);
        atm.UnloadCassettes([1]);

        Assert.Collection(
            atm.NewMovements,
            m => Assert.Equal((MovementType.Load, Currency.TRY, 100, 40), (m.Type, m.Currency, m.Denomination, m.Quantity)),
            m => Assert.Equal((MovementType.Load, Currency.USD, 100, 10), (m.Type, m.Currency, m.Denomination, m.Quantity)),
            m => Assert.Equal((MovementType.Unload, Currency.TRY, 100, -40), (m.Type, m.Currency, m.Denomination, m.Quantity)));
        Assert.Equal(0, atm.GetCassette(1).Count);
        Assert.Equal(10, atm.GetCassette(4).Count);
    }

    internal static Atm InService()
    {
        var atm = Atm.Create("IST-1");
        atm.ConfigureCassettes(StandardConfig);
        atm.LoadCash([new CassetteLoad(1, 50), new CassetteLoad(2, 50), new CassetteLoad(3, 50), new CassetteLoad(4, 50)]);
        atm.PutInService();
        return atm;
    }
}
