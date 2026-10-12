namespace AtmCash.Domain.Cash;

/// <summary>Banknote denominations supported for every currency (coins are not supported).</summary>
public static class Denominations
{
    public static IReadOnlyList<int> All { get; } = [20, 50, 100, 500];

    public static bool IsValid(int denomination) => All.Contains(denomination);
}
