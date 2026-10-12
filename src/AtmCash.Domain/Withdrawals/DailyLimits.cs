using AtmCash.Domain.Cash;

namespace AtmCash.Domain.Withdrawals;

/// <summary>Daily withdrawal limit per card, tracked separately for each currency (B5).</summary>
public static class DailyLimits
{
    public static int For(Currency currency) => currency switch
    {
        Currency.TRY => 20_000,
        Currency.USD => 1_000,
        Currency.EUR => 1_000,
        _ => throw new ArgumentOutOfRangeException(nameof(currency), currency, null),
    };
}
