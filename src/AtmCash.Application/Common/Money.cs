namespace AtmCash.Application.Common;

internal static class Money
{
    /// <summary>
    /// Rounds a monetary amount to two decimals for API responses (4150.000000 becomes 4150.00).
    /// Adding 0.00m fixes the scale at two, so whole amounts also serialize as e.g. 1000.00.
    /// Calculations keep full precision; only the presented value is rounded.
    /// </summary>
    public static decimal Round(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero) + 0.00m;
}
