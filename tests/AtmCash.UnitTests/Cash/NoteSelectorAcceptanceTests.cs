using AtmCash.Domain.Cash;

namespace AtmCash.UnitTests.Cash;

/// <summary>
/// The B3 acceptance table from the assignment, verbatim. Notes are written as
/// "denomination x count", e.g. "50x2 + 20x3"; the currency does not affect selection.
/// </summary>
public class NoteSelectorAcceptanceTests
{
    [Theory]
    [InlineData(1, "100x0 50x3 20x10", 160, "50x2 + 20x3")]
    [InlineData(5, "500x1 100x0 20x5", 600, "500x1 + 20x5")]
    [InlineData(6, "100x1 50x4 20x5", 200, "100x1 + 50x2")]
    [InlineData(7, "500x1 100x3 20x20", 1000, "500x1 + 100x3 + 20x10")]
    public void Pays_exact_amount_with_fewest_notes(int testNo, string available, int requested, string expected)
    {
        var result = NoteSelector.Select(Notes.ParseStock(available), requested);

        Assert.True(result.IsExact, $"Test {testNo}: expected an exact match");
        Assert.Equal(Notes.Parse(expected), result.Exact!.Notes);
        Assert.Equal(requested, result.Exact.Amount);
        Assert.Null(result.Lower);
        Assert.Null(result.Upper);
    }

    [Theory]
    [InlineData(2, "50x1 20x2", 80, 70, "50x1 + 20x1", 90, "50x1 + 20x2")]
    [InlineData(3, "100x5 20x10", 130, 120, "100x1 + 20x1", 140, "100x1 + 20x2")]
    [InlineData(4, "100x2", 250, 200, "100x2", null, null)]
    [InlineData(8, "100x3 50x1 20x1", 110, 100, "100x1", 120, "100x1 + 20x1")]
    public void Offers_lower_and_upper_alternatives_when_not_exact(
        int testNo, string available, int requested,
        int lowerAmount, string lowerNotes, int? upperAmount, string? upperNotes)
    {
        var result = NoteSelector.Select(Notes.ParseStock(available), requested);

        Assert.False(result.IsExact, $"Test {testNo}: expected no exact match");

        Assert.NotNull(result.Lower);
        Assert.Equal(lowerAmount, result.Lower.Amount);
        Assert.Equal(Notes.Parse(lowerNotes), result.Lower.Notes);

        if (upperAmount is null)
        {
            Assert.Null(result.Upper);
        }
        else
        {
            Assert.NotNull(result.Upper);
            Assert.Equal(upperAmount, result.Upper.Amount);
            Assert.Equal(Notes.Parse(upperNotes!), result.Upper.Notes);
        }
    }
}
