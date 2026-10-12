using AtmCash.Domain.Cash;

namespace AtmCash.UnitTests.Cash;

public class NoteSelectorTests
{
    [Fact]
    public void Upper_alternative_is_omitted_when_it_exceeds_the_cap()
    {
        var result = NoteSelector.Select(Notes.ParseStock("50x1 20x2"), 80, upperAmountCap: 89);

        Assert.Equal(70, result.Lower!.Amount);
        Assert.Null(result.Upper);
    }

    [Fact]
    public void Upper_alternative_equal_to_the_cap_is_offered()
    {
        var result = NoteSelector.Select(Notes.ParseStock("50x1 20x2"), 80, upperAmountCap: 90);

        Assert.Equal(90, result.Upper!.Amount);
    }

    [Fact]
    public void Request_above_total_stock_offers_everything_as_lower()
    {
        var result = NoteSelector.Select(Notes.ParseStock("100x2 20x1"), 5000);

        Assert.Equal(220, result.Lower!.Amount);
        Assert.Equal(Notes.Parse("100x2 + 20x1"), result.Lower.Notes);
        Assert.Null(result.Upper);
    }

    [Fact]
    public void Request_below_smallest_payable_amount_has_no_lower()
    {
        var result = NoteSelector.Select(Notes.ParseStock("50x2"), 30);

        Assert.Null(result.Lower);
        Assert.Equal(50, result.Upper!.Amount);
    }

    [Fact]
    public void Amount_that_is_not_a_multiple_of_ten_is_never_exact()
    {
        var result = NoteSelector.Select(Notes.ParseStock("20x5"), 45);

        Assert.Equal(40, result.Lower!.Amount);
        Assert.Equal(60, result.Upper!.Amount);
    }

    [Fact]
    public void Empty_stock_has_no_options()
    {
        var result = NoteSelector.Select(new Dictionary<int, int>(), 100);

        Assert.Equal(new WithdrawalOptions(null, null, null), result);
    }

    [Fact]
    public void Handles_a_full_machine_within_bounds()
    {
        // Worst realistic stock: four full recycling cassettes, one per denomination.
        var result = NoteSelector.Select(Notes.ParseStock("500x100 100x100 50x100 20x100"), 66_990);

        Assert.False(result.IsExact);
        Assert.Equal(66_980, result.Lower!.Amount);
        Assert.Equal(67_000, result.Upper!.Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-20)]
    public void Rejects_non_positive_amount(int amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NoteSelector.Select(Notes.ParseStock("20x1"), amount));
    }

    [Fact]
    public void Rejects_unsupported_denomination()
    {
        Assert.Throws<ArgumentException>(() => NoteSelector.Select(new Dictionary<int, int> { [10] = 5 }, 50));
    }

    [Fact]
    public void Rejects_negative_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NoteSelector.Select(new Dictionary<int, int> { [20] = -1 }, 20));
    }
}
