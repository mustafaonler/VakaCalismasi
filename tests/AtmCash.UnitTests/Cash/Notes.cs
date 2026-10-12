using System.Globalization;
using AtmCash.Domain.Cash;

namespace AtmCash.UnitTests.Cash;

/// <summary>Parses the "denomination x count" notation used in the assignment's tables.</summary>
internal static class Notes
{
    /// <summary>"100x0 50x3 20x10" → stock per denomination (zero counts kept).</summary>
    public static Dictionary<int, int> ParseStock(string text) =>
        Tokens(text).ToDictionary(t => t.Denomination, t => t.Count);

    /// <summary>"50x2 + 20x3" → note lines, largest denomination first.</summary>
    public static NoteLine[] Parse(string text) =>
        Tokens(text).OrderByDescending(t => t.Denomination).ToArray();

    private static IEnumerable<NoteLine> Tokens(string text) =>
        text.Split([' ', '+'], StringSplitOptions.RemoveEmptyEntries).Select(token =>
        {
            var parts = token.Split('x');
            return new NoteLine(
                int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture));
        });
}
