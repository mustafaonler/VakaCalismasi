using AtmCash.Domain.Cash;

namespace AtmCash.Domain.Atms;

/// <summary>
/// Notes of one currency and denomination inside a cassette. A recycling cassette has at most
/// one holding; the deposit cassette has one per currency/denomination it has received.
/// </summary>
public sealed class CassetteHolding
{
    private CassetteHolding()
    {
    }

    internal CassetteHolding(Currency currency, int denomination)
    {
        Currency = currency;
        Denomination = denomination;
    }

    public Currency Currency { get; private set; }

    public int Denomination { get; private set; }

    public int Count { get; internal set; }

    /// <summary>Notes promised to an open withdrawal quote; still physically in the cassette.</summary>
    public int Reserved { get; internal set; }

    public int Available => Count - Reserved;
}
