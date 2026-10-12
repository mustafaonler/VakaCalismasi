namespace AtmCash.Domain.Cash;

/// <summary>TRY value of one unit of a currency, used to value reconciliation differences and totals.</summary>
public sealed class ExchangeRate
{
    private ExchangeRate()
    {
    }

    public ExchangeRate(Currency currency, decimal rateToTry)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rateToTry);
        Currency = currency;
        RateToTry = rateToTry;
    }

    public Currency Currency { get; private set; }

    public decimal RateToTry { get; private set; }
}
